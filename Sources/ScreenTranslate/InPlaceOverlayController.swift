import AppKit
import SwiftUI
#if canImport(ScreenTranslateCore)
import ScreenTranslateCore
#endif

/// 画面上の1つのまとまり（カードおよびインプレース重畳で共通使用する表示モデル）
struct ClusterCardItem: Identifiable, Equatable {
    let id: String
    let indexNumber: Int
    var sourceText: String
    var globalRect: CGRect
    var translatedText: String
    var streamingText: String
    var isTranslating: Bool

    var displayBodyText: String {
        if !streamingText.isEmpty {
            return streamingText
        }
        return translatedText
    }

    var badgeLabel: String {
        String(format: "%02d", indexNumber)
    }
}

@MainActor
final class InPlaceOverlayViewModel: ObservableObject {
    struct PositionedChip: Identifiable, Equatable {
        let id: String
        let badgeLabel: String
        let text: String
        let isStreaming: Bool
        /// オーバーレイパネル左上を原点とした SwiftUI ローカル座標 (x, y, width, minHeight)
        let topLeftX: CGFloat
        let topLeftY: CGFloat
        let boxWidth: CGFloat
        let minBoxHeight: CGFloat
        let globalChipRect: CGRect
        var isCursorHovering: Bool
    }

    @Published var chips: [PositionedChip] = []
}

private struct InPlaceOverlayCanvasView: View {
    @ObservedObject var model: InPlaceOverlayViewModel
    private let activeEmerald = Color(red: 0x4D / 255.0, green: 0xA8 / 255.0, blue: 0x92 / 255.0)

    var body: some View {
        ZStack(alignment: .topLeading) {
            Color.clear
            ForEach(model.chips) { chip in
                HStack(alignment: .top, spacing: 6) {
                    Text(chip.text)
                        .font(.system(size: 13.5, weight: .medium))
                        .foregroundStyle(Color.white.opacity(chip.isStreaming ? 0.85 : 0.96))
                        .lineSpacing(3)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, 9)
                .padding(.vertical, 6)
                .frame(width: chip.boxWidth, alignment: .topLeading)
                .frame(minHeight: chip.minBoxHeight, alignment: .topLeading)
                .background(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .fill(Color(red: 0.06, green: 0.08, blue: 0.11).opacity(0.90))
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 7, style: .continuous)
                        .strokeBorder(
                            chip.isStreaming ? activeEmerald.opacity(0.75) : Color.white.opacity(0.20),
                            lineWidth: 0.8
                        )
                )
                .opacity(chip.isCursorHovering ? 0.12 : 1.0)
                .offset(x: chip.topLeftX, y: chip.topLeftY)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .allowsHitTesting(false)
    }
}

/// ゲーム画面の該当クラスタ位置に直接訳文チップを重畳表示する完全クリック透過オーバーレイ管理クラス。
/// - SCContentFilter(excludingApplications:) により自プロセスのためキャプチャへ一切映り込まない（無限ループゼロ）。
/// - ignoresMouseEvents = true により裏側のゲームやブラウザ操作を 100% 透過する。
/// - マウスカーソルがチップに近づいた瞬間のみ該当チップを α=0.12 に退避させ、裏の原文やUIを見せる。
@MainActor
final class InPlaceOverlayController {
    private let viewModel = InPlaceOverlayViewModel()
    private var panel: NSPanel?
    private var cursorTimer: Timer?
    private var overlayGlobalFrame: CGRect = .zero

    var isVisible: Bool {
        panel?.isVisible ?? false
    }

    func update(items: [ClusterCardItem], screenFrame: CGRect) {
        let readyItems = items.filter { !$0.displayBodyText.isEmpty }
        guard !readyItems.isEmpty, screenFrame.width >= 64, screenFrame.height >= 64 else {
            hide()
            return
        }

        ensurePanel(covering: screenFrame)
        let mousePt = NSEvent.mouseLocation

        var built: [InPlaceOverlayViewModel.PositionedChip] = []
        built.reserveCapacity(readyItems.count)

        for item in readyItems {
            // 見出しなど横幅が極端に狭い枠でも日本語訳が潰れないよう最小幅 160pt を確保し、画面内にクランプする
            let desiredW = min(max(160.0, item.globalRect.width + 12.0), min(580.0, screenFrame.width - 16.0))
            let centeredX = item.globalRect.midX - desiredW * 0.5
            let clampedGlobalX = max(
                screenFrame.minX + 8.0,
                min(centeredX, screenFrame.maxX - desiredW - 8.0)
            )
            let localX = clampedGlobalX - screenFrame.minX
            let localTopY = max(4.0, min(screenFrame.maxY - item.globalRect.maxY, screenFrame.height - 32.0))
            let minH = max(26.0, min(item.globalRect.height, 140.0))

            // マウス退避判定用の推定チップグローバル矩形
            let estimatedHeight = max(minH, CGFloat((item.displayBodyText.count / 18) + 1) * 22.0 + 12.0)
            let chipGlobal = CGRect(
                x: clampedGlobalX,
                y: screenFrame.maxY - localTopY - estimatedHeight,
                width: desiredW,
                height: estimatedHeight
            )
            let hovering = ClusterGeometry.shouldFadeChipForCursor(
                chipGlobalRect: chipGlobal,
                globalMousePoint: mousePt
            )

            built.append(
                InPlaceOverlayViewModel.PositionedChip(
                    id: item.id,
                    badgeLabel: item.badgeLabel,
                    text: item.displayBodyText,
                    isStreaming: item.isTranslating,
                    topLeftX: localX,
                    topLeftY: localTopY,
                    boxWidth: desiredW,
                    minBoxHeight: minH,
                    globalChipRect: chipGlobal,
                    isCursorHovering: hovering
                )
            )
        }

        if viewModel.chips != built {
            viewModel.chips = built
        }
        startCursorAvoidanceTimerIfNeeded()
    }

    func hide() {
        cursorTimer?.invalidate()
        cursorTimer = nil
        if !viewModel.chips.isEmpty {
            viewModel.chips = []
        }
        panel?.orderOut(nil)
    }

    private func ensurePanel(covering screenFrame: CGRect) {
        if let existing = panel {
            if overlayGlobalFrame != screenFrame {
                overlayGlobalFrame = screenFrame
                existing.setFrame(screenFrame, display: true)
            }
            if !existing.isVisible {
                existing.orderFrontRegardless()
            }
            return
        }

        let p = NSPanel(
            contentRect: screenFrame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        p.isFloatingPanel = true
        p.level = .statusBar
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        p.hidesOnDeactivate = false
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.ignoresMouseEvents = true

        let host = NSHostingView(rootView: InPlaceOverlayCanvasView(model: viewModel))
        host.sizingOptions = []
        p.contentView = host
        p.setFrame(screenFrame, display: true)
        p.orderFrontRegardless()

        self.overlayGlobalFrame = screenFrame
        self.panel = p
    }

    private func startCursorAvoidanceTimerIfNeeded() {
        guard cursorTimer == nil else { return }
        cursorTimer = Timer.scheduledTimer(withTimeInterval: 0.10, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                self?.refreshCursorHoverState()
            }
        }
    }

    private func refreshCursorHoverState() {
        guard !viewModel.chips.isEmpty else { return }
        let mousePt = NSEvent.mouseLocation
        var changed = false
        var updated = viewModel.chips
        for idx in updated.indices {
            let nowHover = ClusterGeometry.shouldFadeChipForCursor(
                chipGlobalRect: updated[idx].globalChipRect,
                globalMousePoint: mousePt
            )
            if updated[idx].isCursorHovering != nowHover {
                updated[idx].isCursorHovering = nowHover
                changed = true
            }
        }
        if changed {
            viewModel.chips = updated
        }
    }
}
