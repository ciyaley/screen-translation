import Foundation
import CoreVideo

/// ゼロアロケーションに近い超軽量フレーム差分検知（32x16 = 512点輝度グリッド + 128bit dHash）。
/// 文字の一部変化やスクロールを逃さず検知しつつ、映像圧縮の微小ノイズ（±10未満の輝度揺れ）は誤発火させない。
public enum FrameHash {
    public struct Digest: Equatable, Sendable {
        public let lo: UInt64
        public let hi: UInt64
        public let meanLuma: UInt8
        public let gridLuma: [UInt8]

        public init(lo: UInt64, hi: UInt64, meanLuma: UInt8, gridLuma: [UInt8] = []) {
            self.lo = lo
            self.hi = hi
            self.meanLuma = meanLuma
            self.gridLuma = gridLuma
        }
    }

    public static func compute(pixelBuffer: CVPixelBuffer) -> Digest? {
        guard CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly) == kCVReturnSuccess else {
            return nil
        }
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        let gridCols = 32
        let gridRows = 16
        var grid = [UInt8]()
        grid.reserveCapacity(gridCols * gridRows)

        if CVPixelBufferIsPlanar(pixelBuffer), CVPixelBufferGetPlaneCount(pixelBuffer) >= 1 {
            guard let base = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, 0) else { return nil }
            let width = CVPixelBufferGetWidthOfPlane(pixelBuffer, 0)
            let height = CVPixelBufferGetHeightOfPlane(pixelBuffer, 0)
            let bytesPerRow = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, 0)
            guard width >= 17, height >= 8 else { return nil }

            let ptr = base.assumingMemoryBound(to: UInt8.self)
            var lo: UInt64 = 0
            var hi: UInt64 = 0
            var lumaSum: Int = 0
            var bitIndex: Int = 0

            for r in 0..<gridRows {
                let y = (r * (height - 1)) / (gridRows - 1)
                let rowPtr = ptr.advanced(by: y * bytesPerRow)
                for c in 0..<gridCols {
                    let x = (c * (width - 1)) / (gridCols - 1)
                    let luma = rowPtr[x]
                    grid.append(luma)
                    lumaSum += Int(luma)
                }
            }

            for row in 0..<8 {
                let y = (row * (height - 1)) / 7
                let rowPtr = ptr.advanced(by: y * bytesPerRow)
                var prevLuma = Int(rowPtr[0])
                for col in 1...16 {
                    let x = (col * (width - 1)) / 16
                    let curLuma = Int(rowPtr[x])
                    if curLuma > prevLuma + 5 {
                        if bitIndex < 64 {
                            lo |= (1 << UInt64(bitIndex))
                        } else {
                            hi |= (1 << UInt64(bitIndex - 64))
                        }
                    }
                    prevLuma = curLuma
                    bitIndex += 1
                }
            }
            return Digest(lo: lo, hi: hi, meanLuma: UInt8(clamping: lumaSum / grid.count), gridLuma: grid)
        }

        // 非プレーナ 32BGRA バッファ
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        guard width >= 17, height >= 8 else { return nil }

        let ptr = base.assumingMemoryBound(to: UInt8.self)
        var lo: UInt64 = 0
        var hi: UInt64 = 0
        var lumaSum: Int = 0
        var bitIndex: Int = 0

        for r in 0..<gridRows {
            let y = (r * (height - 1)) / (gridRows - 1)
            let rowPtr = ptr.advanced(by: y * bytesPerRow)
            for c in 0..<gridCols {
                let x = (c * (width - 1)) / (gridCols - 1)
                let luma = sampleBGRALuma(rowPtr: rowPtr, x: x)
                grid.append(UInt8(clamping: luma))
                lumaSum += luma
            }
        }

        for row in 0..<8 {
            let y = (row * (height - 1)) / 7
            let rowPtr = ptr.advanced(by: y * bytesPerRow)
            var prevLuma = sampleBGRALuma(rowPtr: rowPtr, x: 0)
            for col in 1...16 {
                let x = (col * (width - 1)) / 16
                let curLuma = sampleBGRALuma(rowPtr: rowPtr, x: x)
                if curLuma > prevLuma + 5 {
                    if bitIndex < 64 {
                        lo |= (1 << UInt64(bitIndex))
                    } else {
                        hi |= (1 << UInt64(bitIndex - 64))
                    }
                }
                prevLuma = curLuma
                bitIndex += 1
            }
        }

        return Digest(lo: lo, hi: hi, meanLuma: UInt8(clamping: lumaSum / grid.count), gridLuma: grid)
    }

    @inline(__always)
    private static func sampleBGRALuma(rowPtr: UnsafePointer<UInt8>, x: Int) -> Int {
        let px = rowPtr.advanced(by: x * 4)
        return (Int(px[2]) * 77 + Int(px[1]) * 150 + Int(px[0]) * 29) >> 8
    }

    public static func hammingDistance(_ a: Digest, _ b: Digest) -> Int {
        (a.lo ^ b.lo).nonzeroBitCount + (a.hi ^ b.hi).nonzeroBitCount
    }

    /// 512点の輝度差（閾値 > 10）が2点以上変わったか、または平均輝度/dHashが大きく変化した場合に「変化あり」と判定する
    public static func isEffectivelyUnchanged(_ a: Digest, _ b: Digest) -> Bool {
        if !a.gridLuma.isEmpty && a.gridLuma.count == b.gridLuma.count {
            var changedPoints = 0
            for i in 0..<a.gridLuma.count {
                if abs(Int(a.gridLuma[i]) - Int(b.gridLuma[i])) > 10 {
                    changedPoints += 1
                    if changedPoints >= 2 {
                        return false
                    }
                }
            }
            return abs(Int(a.meanLuma) - Int(b.meanLuma)) <= 4
        }
        let dist = hammingDistance(a, b)
        let lumaDiff = abs(Int(a.meanLuma) - Int(b.meanLuma))
        return dist <= 5 && lumaDiff <= 4
    }
}

/// 前回OCRを実行したフレームからの視覚的変化を即座に検知する軽量ゲート。
/// 静止中は dHash (0.3µs) のみでスキップして CPU を 0% に保ちつつ、
/// 画面が動いた・変化した（スクロール・動画・背景アニメーション等）場合は静止を待たずに即座に OCR 判定へ通す。
public struct FrameChangeGate {
    private var lastOCRDigest: FrameHash.Digest?

    public init() {}

    public mutating func reset() {
        lastOCRDigest = nil
    }

    /// 前回OCRを実行した画面から視覚的変化があれば即 true を返す
    public mutating func shouldRunOCR(for digest: FrameHash.Digest) -> Bool {
        guard let lastOCR = lastOCRDigest else {
            lastOCRDigest = digest
            return true
        }
        if FrameHash.isEffectivelyUnchanged(digest, lastOCR) {
            return false
        }
        lastOCRDigest = digest
        return true
    }
}

public enum StabilityAction: Equatable {
    case ignore
    case provisional(String)
    case commit(String)
}

/// UTF-16コード単位による高速バイグラム比較（Characterグラフェム分割と不要な辞書確保を排除）
public struct TextStabilityGate {
    private var lastObservedNorm: String = ""
    private var lastObservedCore: String = ""
    private var lastCommittedNorm: String = ""
    private var lastCommittedCore: String = ""
    private var lastCommittedCriticalSig: String = ""
    private var consecutiveMatches: Int = 0
    public let requiredStableFrames: Int

    private static let negationWords: Set<String> = [
        "not", "no", "never", "none", "nothing", "neither", "nor", "without",
        "cannot", "cant", "dont", "doesnt", "didnt", "wont", "wouldnt", "isnt", "arent", "wasnt", "werent"
    ]

    public init(requiredStableFrames: Int = 2) {
        self.requiredStableFrames = max(1, requiredStableFrames)
    }

    public mutating func reset() {
        lastObservedNorm = ""
        lastObservedCore = ""
        lastCommittedNorm = ""
        lastCommittedCore = ""
        lastCommittedCriticalSig = ""
        consecutiveMatches = 0
    }

    public static func normalize(_ raw: String) -> String {
        let folded = raw.precomposedStringWithCompatibilityMapping
        var result = ""
        result.reserveCapacity(folded.count)
        var prevSpace = true
        for ch in folded {
            if ch.isWhitespace && ch != "\n" {
                if !prevSpace {
                    result.append(" ")
                    prevSpace = true
                }
            } else {
                result.append(ch)
                prevSpace = (ch == "\n")
            }
        }
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    public static func semanticCore(_ normalized: String) -> String {
        var core = ""
        core.reserveCapacity(normalized.count)
        for sc in normalized.unicodeScalars {
            if sc.properties.isAlphabetic
                || sc.properties.isIdeographic
                || sc.properties.numericType != nil
                || (0x0030...0x0039).contains(sc.value)
                || sc == "+" || sc == "-" {
                core.unicodeScalars.append(sc)
            }
        }
        return core.lowercased()
    }

    public static func criticalSignature(_ normalized: String) -> String {
        let scalars = Array(normalized.unicodeScalars)
        var digits = ""
        var currentAlpha = ""
        var negations: [String] = []

        for idx in 0..<scalars.count {
            let sc = scalars[idx]
            let isAsciiDigit = (0x0030...0x0039).contains(sc.value)
            let isNumeric = isAsciiDigit || sc.properties.numericType != nil

            if isNumeric {
                let prevIsAlpha = (idx > 0) && scalars[idx - 1].properties.isAlphabetic
                let nextIsAlpha = (idx + 1 < scalars.count) && scalars[idx + 1].properties.isAlphabetic
                let prevIsDigit = (idx > 0) && ((0x0030...0x0039).contains(scalars[idx - 1].value) || scalars[idx - 1].properties.numericType != nil)
                let nextIsDigit = (idx + 1 < scalars.count) && ((0x0030...0x0039).contains(scalars[idx + 1].value) || scalars[idx + 1].properties.numericType != nil)
                let isInWordSingleZeroOrOne = (sc == "0" || sc == "1") && prevIsAlpha && nextIsAlpha && !prevIsDigit && !nextIsDigit
                if !isInWordSingleZeroOrOne {
                    digits.unicodeScalars.append(sc)
                }
            }
            if sc.properties.isAlphabetic {
                currentAlpha.unicodeScalars.append(sc)
            } else {
                if !currentAlpha.isEmpty {
                    let clean = currentAlpha.lowercased()
                    if negationWords.contains(clean) {
                        negations.append(clean)
                    }
                    currentAlpha = ""
                }
                let v = sc.value
                if v == 0x4E0D || v == 0x7121 || v == 0x672A || v == 0x975E {
                    negations.append(String(sc))
                }
            }
        }
        if !currentAlpha.isEmpty {
            let clean = currentAlpha.lowercased()
            if negationWords.contains(clean) {
                negations.append(clean)
            }
        }
        if normalized.contains("ない") { negations.append("ない") }
        if normalized.contains("ません") { negations.append("ません") }

        return "\(digits)|\(negations.joined(separator: ","))"
    }

    /// UTF-16コード単位ソート済みバイグラムによる省アロケーション Dice 類似度（ICU Grapheme分割不要）
    public static func similarity(_ a: String, _ b: String) -> Double {
        if a == b { return 1.0 }
        let ua = Array(a.utf16)
        let ub = Array(b.utf16)
        if ua.isEmpty || ub.isEmpty { return 0.0 }

        let maxLen = max(ua.count, ub.count)
        let minLen = min(ua.count, ub.count)
        if Double(minLen) / Double(maxLen) < 0.72 {
            return Double(minLen) / Double(maxLen) * 0.5
        }
        if ua.count == 1 || ub.count == 1 {
            return (ua == ub) ? 1.0 : 0.0
        }

        var ba = ContiguousArray<UInt32>()
        ba.reserveCapacity(ua.count - 1)
        for i in 0..<(ua.count - 1) {
            ba.append((UInt32(ua[i]) << 16) | UInt32(ua[i + 1]))
        }
        ba.sort()

        var bb = ContiguousArray<UInt32>()
        bb.reserveCapacity(ub.count - 1)
        for i in 0..<(ub.count - 1) {
            bb.append((UInt32(ub[i]) << 16) | UInt32(ub[i + 1]))
        }
        bb.sort()

        var ia = 0
        var ib = 0
        var matches = 0
        while ia < ba.count && ib < bb.count {
            let va = ba[ia]
            let vb = bb[ib]
            if va == vb {
                matches += 1
                ia += 1
                ib += 1
            } else if va < vb {
                ia += 1
            } else {
                ib += 1
            }
        }

        return (2.0 * Double(matches)) / Double(ba.count + bb.count)
    }

    public mutating func feed(_ rawText: String, averageConfidence: Float = 0.85) -> StabilityAction {
        let norm = Self.normalize(rawText)
        guard !norm.isEmpty else {
            lastObservedNorm = ""
            lastObservedCore = ""
            consecutiveMatches = 0
            return .ignore
        }

        let core = Self.semanticCore(norm)
        let critSig = Self.criticalSignature(norm)

        if !lastCommittedCore.isEmpty && critSig == lastCommittedCriticalSig {
            if core == lastCommittedCore {
                lastObservedNorm = norm
                lastObservedCore = core
                consecutiveMatches = requiredStableFrames
                return .ignore
            }
            let postCommitThreshold = (core.count < 40) ? 0.88 : 0.91
            if core.count >= 16 && Self.similarity(core, lastCommittedCore) >= postCommitThreshold {
                lastObservedNorm = norm
                lastObservedCore = core
                consecutiveMatches = requiredStableFrames
                return .ignore
            }
        }

        let trimmedRaw = rawText.trimmingCharacters(in: .whitespacesAndNewlines)

        if core.count >= 60 && averageConfidence >= 0.72 {
            lastObservedNorm = norm
            lastObservedCore = core
            lastCommittedNorm = norm
            lastCommittedCore = core
            lastCommittedCriticalSig = critSig
            consecutiveMatches = requiredStableFrames
            return .commit(trimmedRaw)
        }

        let matchesPrev: Bool
        if core == lastObservedCore {
            matchesPrev = true
        } else if core.count >= 20 && !lastObservedCore.isEmpty && Self.similarity(core, lastObservedCore) >= 0.90 {
            matchesPrev = true
        } else {
            matchesPrev = false
        }

        if matchesPrev {
            consecutiveMatches += 1
            lastObservedNorm = norm
            lastObservedCore = core
        } else {
            lastObservedNorm = norm
            lastObservedCore = core
            consecutiveMatches = 1
        }

        if requiredStableFrames == 1 || consecutiveMatches >= requiredStableFrames {
            lastCommittedNorm = norm
            lastCommittedCore = core
            lastCommittedCriticalSig = critSig
            return .commit(trimmedRaw)
        } else {
            return .provisional(trimmedRaw)
        }
    }
}

/// 固定上限付きの軽量LRUキャッシュ
public struct BoundedLRUCache<Key: Hashable, Value> {
    private let capacity: Int
    private var storage: [Key: Value] = [:]
    private var order: [Key] = []

    public init(capacity: Int = 128) {
        self.capacity = max(8, capacity)
        self.storage.reserveCapacity(self.capacity)
        self.order.reserveCapacity(self.capacity)
    }

    public mutating func get(_ key: Key) -> Value? {
        guard let val = storage[key] else { return nil }
        if let idx = order.firstIndex(of: key) {
            order.remove(at: idx)
            order.append(key)
        }
        return val
    }

    public mutating func set(_ key: Key, value: Value) {
        if storage[key] != nil {
            if let idx = order.firstIndex(of: key) {
                order.remove(at: idx)
            }
        } else if order.count >= capacity, let oldest = order.first {
            order.removeFirst()
            storage.removeValue(forKey: oldest)
        }
        storage[key] = value
        order.append(key)
    }

    public mutating func clear() {
        storage.removeAll(keepingCapacity: true)
        order.removeAll(keepingCapacity: true)
    }
}
