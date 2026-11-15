import AppKit
import QuartzCore

/// 選択完了時に返す画面座標（AppKit グローバルスクリーン座標系 + 対象 NSScreen）
struct CaptureSelection {
    let globalRect: CGRect
    let screenFrame: CGRect
    let displayID: CGDirectDisplayID
}

/// ドラッグで監視範囲を指定するオーバーレイ。
/// 選択中のみ表示され、完了またはESCキャンセル時は即座に orderOut して描画負荷ゼロに戻す。
final class RegionSelectorController {
    private var overlayWindows: [SelectorWindow] = []
    private var onComplete: ((CaptureSelection?) -> Void)?

    func beginSelection(completion: @escaping (CaptureSelection?) -> Void) {
        cancel()
        self.onComplete = completion
        NSCursor.crosshair.push()

        for screen in NSScreen.screens {
            let win = SelectorWindow(screen: screen) { [weak self] selection in
                self?.finish(with: selection)
            }
            overlayWindows.append(win)
            win.makeKeyAndOrderFront(nil)
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    func cancel() {
        if !overlayWindows.isEmpty {
            NSCursor.pop()
        }
        for win in overlayWindows {
            win.orderOut(nil)
        }
        overlayWindows.removeAll()
        onComplete = nil
    }

    private func finish(with selection: CaptureSelection?) {
        let cb = onComplete
        cancel()
        cb?(selection)
    }
}

private final class SelectorWindow: NSPanel {
    private let selectionView: SelectorCanvasView
    private let targetScreen: NSScreen
    private let onFinish: (CaptureSelection?) -> Void

    override var canBecomeKey: Bool { true }

    init(screen: NSScreen, onFinish: @escaping (CaptureSelection?) -> Void) {
        self.targetScreen = screen
        self.onFinish = onFinish
        self.selectionView = SelectorCanvasView(frame: NSRect(origin: .zero, size: screen.frame.size))

        super.init(
            contentRect: screen.frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )

        setFrame(screen.frame, display: true)
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        ignoresMouseEvents = false
        contentView = selectionView

        selectionView.onRectCommitted = { [weak self] localRect in
            guard let self else { return }
            guard let localRect, localRect.width >= 24, localRect.height >= 16 else {
                self.onFinish(nil)
                return
            }
            let global = CGRect(
                x: self.targetScreen.frame.origin.x + localRect.origin.x,
                y: self.targetScreen.frame.origin.y + localRect.origin.y,
                width: localRect.width,
                height: localRect.height
            )
            let id = (self.targetScreen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? CGMainDisplayID()
            self.onFinish(CaptureSelection(globalRect: global, screenFrame: self.targetScreen.frame, displayID: id))
        }
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { // ESC
            onFinish(nil)
        } else {
            super.keyDown(with: event)
        }
    }
}

private final class SelectorCanvasView: NSView {
    var onRectCommitted: ((CGRect?) -> Void)?
    private var dragStart: CGPoint?
    private var currentPoint: CGPoint?

    private let scrimLayer = CAShapeLayer()
    private let hairlineLayer = CAShapeLayer()
    private let bracketLayer = CAShapeLayer()
    private let sizeLabel = CATextLayer()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true

        scrimLayer.fillRule = .evenOdd
        scrimLayer.fillColor = DesignTokens.selectionScrim.cgColor
        layer?.addSublayer(scrimLayer)

        hairlineLayer.fillColor = nil
        hairlineLayer.strokeColor = NSColor(calibratedWhite: 1.0, alpha: 0.35).cgColor
        hairlineLayer.lineWidth = 1.0
        layer?.addSublayer(hairlineLayer)

        bracketLayer.fillColor = nil
        bracketLayer.strokeColor = DesignTokens.accentEmerald.cgColor
        bracketLayer.lineWidth = 1.5
        layer?.addSublayer(bracketLayer)

        sizeLabel.fontSize = 11.0
        sizeLabel.font = NSFont.monospacedSystemFont(ofSize: 11.0, weight: .medium)
        sizeLabel.foregroundColor = DesignTokens.accentEmerald.cgColor
        sizeLabel.contentsScale = NSScreen.main?.backingScaleFactor ?? 2.0
        sizeLabel.isHidden = true
        layer?.addSublayer(sizeLabel)

        updateLayers(selection: nil)
    }

    required init?(coder: NSCoder) { fatalError() }

    override func mouseDown(with event: NSEvent) {
        let pt = convert(event.locationInWindow, from: nil)
        dragStart = pt
        currentPoint = pt
        updateLayers(selection: CGRect(origin: pt, size: .zero))
    }

    override func mouseDragged(with event: NSEvent) {
        guard let start = dragStart else { return }
        let pt = convert(event.locationInWindow, from: nil)
        currentPoint = pt
        let rect = CGRect(
            x: min(start.x, pt.x),
            y: min(start.y, pt.y),
            width: abs(pt.x - start.x),
            height: abs(pt.y - start.y)
        ).integral
        updateLayers(selection: rect)
    }

    override func mouseUp(with event: NSEvent) {
        guard let start = dragStart, let end = currentPoint else {
            onRectCommitted?(nil)
            return
        }
        let rect = CGRect(
            x: min(start.x, end.x),
            y: min(start.y, end.y),
            width: abs(end.x - start.x),
            height: abs(end.y - start.y)
        ).integral
        dragStart = nil
        currentPoint = nil
        onRectCommitted?(rect)
    }

    private func updateLayers(selection: CGRect?) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)

        let outer = CGMutablePath()
        outer.addRect(bounds)
        if let sel = selection, sel.width > 1, sel.height > 1 {
            outer.addRect(sel)
            scrimLayer.path = outer
            hairlineLayer.path = CGPath(rect: sel.insetBy(dx: 0.5, dy: 0.5), transform: nil)
            bracketLayer.path = Self.cornerBrackets(in: sel, arm: 12.0)

            sizeLabel.isHidden = false
            sizeLabel.string = "\(Int(sel.width)) × \(Int(sel.height))"
            sizeLabel.frame = CGRect(x: sel.minX, y: max(8, sel.minY - 20), width: 140, height: 16)
        } else {
            scrimLayer.path = outer
            hairlineLayer.path = nil
            bracketLayer.path = nil
            sizeLabel.isHidden = true
        }

        CATransaction.commit()
    }

    static func cornerBrackets(in r: CGRect, arm: CGFloat) -> CGPath {
        let p = CGMutablePath()
        let l = min(arm, min(r.width, r.height) * 0.4)
        p.move(to: CGPoint(x: r.minX, y: r.maxY - l)); p.addLine(to: CGPoint(x: r.minX, y: r.maxY)); p.addLine(to: CGPoint(x: r.minX + l, y: r.maxY))
        p.move(to: CGPoint(x: r.maxX - l, y: r.maxY)); p.addLine(to: CGPoint(x: r.maxX, y: r.maxY)); p.addLine(to: CGPoint(x: r.maxX, y: r.maxY - l))
        p.move(to: CGPoint(x: r.minX, y: r.minY + l)); p.addLine(to: CGPoint(x: r.minX, y: r.minY)); p.addLine(to: CGPoint(x: r.minX + l, y: r.minY))
        p.move(to: CGPoint(x: r.maxX - l, y: r.minY)); p.addLine(to: CGPoint(x: r.maxX, y: r.minY)); p.addLine(to: CGPoint(x: r.maxX, y: r.minY + l))
        return p
    }
}

/// 稼働中の監視範囲やカードホバー中の該当クラスタ位置を示す軽量インジケータ（非表示時は orderOut され合成負荷ゼロ）
final class RegionIndicatorPanel: NSPanel {
    private let bracketLayer = CAShapeLayer()
    private let fillLayer = CAShapeLayer()
    private let badgeLayer = CATextLayer()

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 100, height: 60),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        hidesOnDeactivate = false
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        ignoresMouseEvents = true

        let v = NSView(frame: NSRect(x: 0, y: 0, width: 100, height: 60))
        v.wantsLayer = true

        fillLayer.fillColor = DesignTokens.accentEmerald.withAlphaComponent(0.08).cgColor
        fillLayer.strokeColor = nil
        fillLayer.isHidden = true
        v.layer?.addSublayer(fillLayer)

        bracketLayer.fillColor = nil
        bracketLayer.strokeColor = DesignTokens.accentEmerald.withAlphaComponent(0.85).cgColor
        bracketLayer.lineWidth = 1.5
        v.layer?.addSublayer(bracketLayer)

        badgeLayer.fontSize = 10.5
        badgeLayer.font = NSFont.monospacedSystemFont(ofSize: 10.5, weight: .bold)
        badgeLayer.foregroundColor = NSColor.white.cgColor
        badgeLayer.backgroundColor = NSColor(calibratedRed: 0.08, green: 0.11, blue: 0.14, alpha: 0.90).cgColor
        badgeLayer.alignmentMode = .center
        badgeLayer.cornerRadius = 4.0
        badgeLayer.contentsScale = NSScreen.main?.backingScaleFactor ?? 2.0
        badgeLayer.isHidden = true
        v.layer?.addSublayer(badgeLayer)

        contentView = v
    }

    func showAround(globalRect: CGRect, badgeText: String? = nil, emphasized: Bool = false) {
        let topPad: CGFloat = (badgeText != nil) ? 18.0 : 3.0
        let panelRect = CGRect(
            x: globalRect.minX - 4,
            y: globalRect.minY - 4,
            width: max(28, globalRect.width + 8),
            height: max(22, globalRect.height + 4 + topPad)
        )
        setFrame(panelRect, display: true)
        contentView?.frame = NSRect(origin: .zero, size: panelRect.size)

        CATransaction.begin()
        CATransaction.setDisableActions(true)

        let boxRect = CGRect(
            x: 4,
            y: 4,
            width: max(20, globalRect.width),
            height: max(14, globalRect.height)
        )
        bracketLayer.strokeColor = DesignTokens.accentEmerald.withAlphaComponent(emphasized ? 0.95 : 0.65).cgColor
        bracketLayer.lineWidth = emphasized ? 1.8 : 1.2
        bracketLayer.path = SelectorCanvasView.cornerBrackets(in: boxRect, arm: 9.0)

        if emphasized {
            fillLayer.isHidden = false
            fillLayer.path = CGPath(roundedRect: boxRect, cornerWidth: 4, cornerHeight: 4, transform: nil)
        } else {
            fillLayer.isHidden = true
        }

        if let badgeText, !badgeText.isEmpty {
            badgeLayer.isHidden = false
            badgeLayer.string = badgeText
            badgeLayer.frame = CGRect(x: 4, y: boxRect.maxY + 2, width: 26, height: 15)
        } else {
            badgeLayer.isHidden = true
        }

        CATransaction.commit()
        orderFrontRegardless()
    }
}
