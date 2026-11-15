import Foundation
import AppKit
import ScreenCaptureKit
import Vision
import CoreMedia
import CoreVideo
import VideoToolbox
#if canImport(ScreenTranslateCore)
import ScreenTranslateCore
#endif

enum CapturedTextEvent: Equatable {
    case ocrStarted
    case provisional
    case commitClusters(clusters: [ExtractedTextCluster])
    case skipped(reason: SkipReason)
    case idle
}

final class CaptureEngine: NSObject, SCStreamOutput, SCStreamDelegate {
    var onTextEvent: ((CapturedTextEvent) -> Void)?
    var onStatusError: ((String) -> Void)?

    private var sourceLangCode: String = "auto"
    private var targetLangCode: String = "ja"

    private let captureQueue = DispatchQueue(label: "screen.translate.capture", qos: .userInitiated)

    private var stream: SCStream?
    private var frameChangeGate = FrameChangeGate()
    private var lastCommittedLayoutKey: String = ""
    private var lastEmittedSkipReason: SkipReason?
    private var isProcessingOCR = false
    private var isTranslationBusy = false
    /// 翻訳中に画面が変化した場合のみ IOSurface から切り離した CGImage を 1 枚保持（SCStream プール枯渇を完全防止）
    private var pendingDeferredImage: CGImage?
    private var cachedSupportedLanguages: [String]?

    /// 画面全体の負荷を最小化するキャプチャ解像度上限
    private static let maxCaptureDimension: CGFloat = 1080.0

    var isRunning: Bool { stream != nil }

    func updateLanguages(sourceCode: String, targetCode: String) {
        captureQueue.async { [weak self] in
            self?.sourceLangCode = sourceCode
            self?.targetLangCode = targetCode
            self?.frameChangeGate.reset()
            self?.lastCommittedLayoutKey = ""
            self?.lastEmittedSkipReason = nil
            self?.pendingDeferredImage = nil
        }
    }

    func invalidateFrameCache() {
        captureQueue.async { [weak self] in
            self?.frameChangeGate.reset()
            self?.lastCommittedLayoutKey = ""
        }
    }

    func setTranslationBusy(_ busy: Bool) {
        captureQueue.async { [weak self] in
            guard let self else { return }
            self.isTranslationBusy = busy
            if !busy, !self.isProcessingOCR, let deferredCG = self.pendingDeferredImage {
                self.pendingDeferredImage = nil
                self.processDeferredCGImage(deferredCG)
            }
        }
    }

    func start(selection: CaptureSelection, sourceLangCode: String, targetLangCode: String) async throws {
        await stop()

        self.sourceLangCode = sourceLangCode
        self.targetLangCode = targetLangCode
        frameChangeGate.reset()
        lastCommittedLayoutKey = ""
        lastEmittedSkipReason = nil
        isTranslationBusy = false
        pendingDeferredImage = nil

        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == selection.displayID }) ?? content.displays.first else {
            throw NSError(domain: "ScreenTranslate", code: 1, userInfo: [NSLocalizedDescriptionKey: "対象ディスプレイが見つかりません"])
        }

        let screenFrame = selection.screenFrame
        let localX = selection.globalRect.minX - screenFrame.minX
        let localTopY = screenFrame.maxY - selection.globalRect.maxY
        let clampedX = max(0, min(localX, CGFloat(display.width) - 16))
        let clampedY = max(0, min(localTopY, CGFloat(display.height) - 16))
        let clampedW = max(16, min(selection.globalRect.width, CGFloat(display.width) - clampedX))
        let clampedH = max(16, min(selection.globalRect.height, CGFloat(display.height) - clampedY))
        let sourceRect = CGRect(x: clampedX, y: clampedY, width: clampedW, height: clampedH)

        let screenScale: CGFloat = NSScreen.screens.first(where: {
            (($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value) == selection.displayID
        })?.backingScaleFactor ?? 2.0

        let effectiveScale = min(screenScale, (max(clampedW, clampedH) > 480) ? 1.35 : 1.6)
        let scaledW = clampedW * effectiveScale
        let scaledH = clampedH * effectiveScale
        let downscale = min(1.0, Self.maxCaptureDimension / max(scaledW, scaledH))

        let currentPID = ProcessInfo.processInfo.processIdentifier
        let excludedApps = content.applications.filter { $0.processID == currentPID }
        let filter = SCContentFilter(display: display, excludingApplications: excludedApps, exceptingWindows: [])

        let config = SCStreamConfiguration()
        config.sourceRect = sourceRect
        config.width = max(32, Int((scaledW * downscale).rounded()) & ~1)
        config.height = max(32, Int((scaledH * downscale).rounded()) & ~1)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = false
        config.capturesAudio = false
        config.queueDepth = 3
        config.minimumFrameInterval = CMTime(value: 4, timescale: 10)

        let newStream = SCStream(filter: filter, configuration: config, delegate: self)
        try newStream.addStreamOutput(self, type: .screen, sampleHandlerQueue: captureQueue)
        try await newStream.startCapture()
        self.stream = newStream
    }

    func stop() async {
        guard let existing = stream else { return }
        stream = nil
        pendingDeferredImage = nil
        try? await existing.stopCapture()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .screen, sampleBuffer.isValid else { return }

        autoreleasepool {
            if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
               let info = attachments.first,
               let rawStatus = info[.status] as? Int,
               let status = SCFrameStatus(rawValue: rawStatus),
               status != .complete {
                return
            }

            guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
            guard CVPixelBufferGetWidth(pixelBuffer) >= 24,
                  CVPixelBufferGetHeight(pixelBuffer) >= 16 else {
                return
            }

            if isTranslationBusy || isProcessingOCR {
                if let digest = FrameHash.compute(pixelBuffer: pixelBuffer),
                   frameChangeGate.shouldRunOCR(for: digest) {
                    var cgImg: CGImage?
                    VTCreateCGImageFromCVPixelBuffer(pixelBuffer, options: nil, imageOut: &cgImg)
                    if let cgImg {
                        pendingDeferredImage = cgImg
                    }
                }
                return
            }

            guard let digest = FrameHash.compute(pixelBuffer: pixelBuffer),
                  frameChangeGate.shouldRunOCR(for: digest) else {
                return
            }

            runOCRAndEmit {
                performVisionOCR(
                    handler: VNImageRequestHandler(cvPixelBuffer: pixelBuffer, orientation: .up, options: [:]),
                    langCode: sourceLangCode
                )
            }
        }
    }

    private func processDeferredCGImage(_ cgImage: CGImage) {
        runOCRAndEmit {
            performVisionOCR(
                handler: VNImageRequestHandler(cgImage: cgImage, orientation: .up, options: [:]),
                langCode: sourceLangCode
            )
        }
    }

    private func runOCRAndEmit(_ ocrBlock: () -> [OCRTextObservation]) {
        isProcessingOCR = true
        defer { isProcessingOCR = false }

        DispatchQueue.main.async { [weak self] in
            self?.onTextEvent?(.ocrStarted)
        }

        let rawObservations = ocrBlock()
        let spatial = OCRSpatialFilter.extractPrimaryText(
            from: rawObservations,
            sourceLangCode: sourceLangCode,
            targetLangCode: targetLangCode
        )

        if spatial.clusters.isEmpty {
            lastCommittedLayoutKey = ""
            if let rejectReason = spatial.lastRejectedReason, rejectReason != .empty {
                emitSkipIfChanged(rejectReason)
            } else {
                lastEmittedSkipReason = nil
                DispatchQueue.main.async { [weak self] in
                    self?.onTextEvent?(.idle)
                }
            }
            return
        }

        // 各クラスタを個別に TranslatabilityGate で検証し、翻訳すべきクラスタのみを座標付きで残す
        var validClusters: [ExtractedTextCluster] = []
        validClusters.reserveCapacity(spatial.clusters.count)
        var lastSkip: SkipReason = .lowLanguageConfidence

        for cl in spatial.clusters {
            switch TranslatabilityGate.evaluate(
                rawText: cl.text,
                selectedSourceCode: sourceLangCode,
                targetCode: targetLangCode
            ) {
            case .skip(let reason):
                lastSkip = reason
            case .translate(let cleanedText, _, _):
                validClusters.append(
                    ExtractedTextCluster(
                        text: cleanedText,
                        averageConfidence: cl.averageConfidence,
                        minX: cl.minX,
                        maxX: cl.maxX,
                        topY: cl.topY,
                        bottomY: cl.bottomY
                    )
                )
            }
        }

        guard !validClusters.isEmpty else {
            lastCommittedLayoutKey = ""
            emitSkipIfChanged(lastSkip)
            return
        }

        // テキスト内容またはクラスタ位置（2%以上の移動＝スクロール追従）に変化があるか判定
        let layoutKey = validClusters.map { cl in
            let qx = Int((cl.minX * 50.0).rounded())
            let qy = Int((cl.topY * 50.0).rounded())
            return "\(cl.semanticCore)@\(qx),\(qy)"
        }.joined(separator: "||")

        if layoutKey == lastCommittedLayoutKey {
            lastEmittedSkipReason = nil
            DispatchQueue.main.async { [weak self] in
                self?.onTextEvent?(.idle)
            }
            return
        }

        lastCommittedLayoutKey = layoutKey
        lastEmittedSkipReason = nil
        DispatchQueue.main.async { [weak self] in
            self?.onTextEvent?(.commitClusters(clusters: validClusters))
        }
    }

    private func emitSkipIfChanged(_ reason: SkipReason) {
        guard lastEmittedSkipReason != reason else { return }
        lastEmittedSkipReason = reason
        DispatchQueue.main.async { [weak self] in
            self?.onTextEvent?(.skipped(reason: reason))
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        DispatchQueue.main.async { [weak self] in
            self?.stream = nil
            self?.onStatusError?(error.localizedDescription)
        }
    }

    private func performVisionOCR(handler: VNImageRequestHandler, langCode: String) -> [OCRTextObservation] {
        let request = VNRecognizeTextRequest()
        request.revision = VNRecognizeTextRequestRevision3
        request.recognitionLevel = .accurate

        let supported: [String]
        if let cached = cachedSupportedLanguages {
            supported = cached
        } else {
            let queried = (try? request.supportedRecognitionLanguages()) ?? ResolvedVisionOCRSpec.defaultAutoPriorityLanguages
            cachedSupportedLanguages = queried
            supported = queried
        }

        let spec = ResolvedVisionOCRSpec.resolve(sourceLangCode: langCode, supportedByOS: supported)
        request.usesLanguageCorrection = spec.usesLanguageCorrection
        request.minimumTextHeight = spec.minimumTextHeight
        request.recognitionLanguages = spec.recognitionLanguages
        if #available(macOS 13.0, *) {
            request.automaticallyDetectsLanguage = spec.automaticallyDetectsLanguage
        }

        do {
            try handler.perform([request])
        } catch {
            return []
        }

        guard let results = request.results, !results.isEmpty else {
            return []
        }

        var collected: [OCRTextObservation] = []
        collected.reserveCapacity(results.count)
        for obs in results {
            let candidates = obs.topCandidates(1)
            guard let top = candidates.first else { continue }
            let box = obs.boundingBox
            collected.append(
                OCRTextObservation(
                    text: top.string,
                    confidence: top.confidence,
                    normX: Double(box.origin.x),
                    normY: Double(box.origin.y),
                    normWidth: Double(box.size.width),
                    normHeight: Double(box.size.height)
                )
            )
        }
        return collected
    }
}
