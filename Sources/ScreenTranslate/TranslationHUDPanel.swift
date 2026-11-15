import AppKit
import SwiftUI
import Translation
#if canImport(ScreenTranslateCore)
import ScreenTranslateCore
#endif

@MainActor
final class HUDViewModel: ObservableObject {
    @Published var sourceLanguage: TranslationLanguage = TranslationLanguage.auto {
        didSet { UserDefaults.standard.set(sourceLanguage.code, forKey: "lang.source") }
    }
    @Published var targetLanguage: TranslationLanguage = TranslationLanguage.concreteLanguages[1] {
        didSet { UserDefaults.standard.set(targetLanguage.code, forKey: "lang.target") }
    }
    @Published var backend: TranslationBackendKind = .plamo2B {
        didSet { UserDefaults.standard.set(backend.rawValue, forKey: "engine.backend") }
    }
    @Published var displayMode: TranslationDisplayMode = .cardList {
        didSet { UserDefaults.standard.set(displayMode.rawValue, forKey: "ui.displayMode") }
    }

    @Published var isCapturing: Bool = false
    @Published var isUpdatingApp: Bool = false
    @Published var canSelfRebuildFromSource: Bool = true
    @Published var isPipelineBusy: Bool = false
    @Published var isCollapsed: Bool = false

    /// ヘッダーの短い進行ステータス
    @Published var pipelineStageLabel: String = ""
    /// まとまり（クラスタ）単位のカード配列
    @Published var clusterCards: [ClusterCardItem] = []
    /// 単一表示・後方互換用のテキストプロパティ
    @Published var streamingTranslatedText: String = ""
    @Published var previousTranslatedText: String = ""
    @Published var translatedText: String = ""
    @Published var statusNotice: String = "範囲ボタン（または ⌥⇧R）で監視領域を選択"

    var onCloseApp: (() -> Void)?
    var onSelectRegion: (() -> Void)?
    var onToggleCapture: (() -> Void)?
    var onUpdateAndRelaunch: (() -> Void)?
    var onToggleCollapse: (() -> Void)?
    var onLanguageChanged: (() -> Void)?
    var onBackendChanged: (() -> Void)?
    var onDisplayModeChanged: (() -> Void)?
    var onHoverClusterCard: ((ClusterCardItem?) -> Void)?

    init() {
        if let savedSrc = UserDefaults.standard.string(forKey: "lang.source"),
           let match = TranslationLanguage.sourceOptions.first(where: { $0.code == savedSrc }) {
            self.sourceLanguage = match
        }
        if let savedTgt = UserDefaults.standard.string(forKey: "lang.target"),
           let match = TranslationLanguage.targetOptions.first(where: { $0.code == savedTgt }) {
            self.targetLanguage = match
        }
        if let savedBackend = UserDefaults.standard.string(forKey: "engine.backend"),
           let match = TranslationBackendKind(rawValue: savedBackend) {
            self.backend = match
        }
        if let savedMode = UserDefaults.standard.string(forKey: "ui.displayMode"),
           let match = TranslationDisplayMode(rawValue: savedMode) {
            self.displayMode = match
        }
    }

    var hasVisibleContent: Bool {
        clusterCards.contains(where: { !$0.displayBodyText.isEmpty || $0.isTranslating })
            || !streamingTranslatedText.isEmpty
            || !translatedText.isEmpty
    }

    func updateStage(_ stage: String, busy: Bool) {
        if pipelineStageLabel != stage {
            pipelineStageLabel = stage
        }
        if isPipelineBusy != busy {
            isPipelineBusy = busy
        }
        if busy && !hasVisibleContent && !stage.isEmpty {
            statusNotice = stage
        }
    }

    func applyClusterCards(_ cards: [ClusterCardItem], busy: Bool, stage: String) {
        self.clusterCards = cards
        let joinedCommitted = cards
            .map(\.displayBodyText)
            .filter { !$0.isEmpty }
            .joined(separator: "\n\n")
        if !joinedCommitted.isEmpty {
            self.translatedText = joinedCommitted
        }
        self.streamingTranslatedText = ""
        updateStage(stage, busy: busy)
    }

    func presentStreamingPartial(_ partial: String) {
        let trimmed = partial.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        streamingTranslatedText = trimmed
        isPipelineBusy = true
    }

    func presentTranslation(_ text: String) {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        if !translatedText.isEmpty && translatedText != trimmed {
            previousTranslatedText = translatedText
        }
        streamingTranslatedText = ""
        translatedText = trimmed
        isPipelineBusy = false
        pipelineStageLabel = isCapturing ? "待機中" : ""
    }

    func presentSkipped(_ reason: SkipReason) {
        isPipelineBusy = false
        streamingTranslatedText = ""
        pipelineStageLabel = reason.shortBadgeJaText.isEmpty ? (isCapturing ? "待機中" : "") : reason.shortBadgeJaText
        if !hasVisibleContent && reason != .empty {
            statusNotice = "翻訳対象外をスキップ中（\(reason.detailJaText)）"
        }
    }

    func clearTexts() {
        clusterCards = []
        previousTranslatedText = ""
        translatedText = ""
        streamingTranslatedText = ""
        pipelineStageLabel = ""
        isPipelineBusy = false
    }
}

/// Apple標準の背後すりガラス効果 (NSVisualEffectView behindWindow + vibrantDark)
struct AppleBackdropBlurView: NSViewRepresentable {
    var material: NSVisualEffectView.Material = .hudWindow

    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = material
        v.blendingMode = .behindWindow
        v.state = .active
        v.appearance = NSAppearance(named: .vibrantDark)
        v.isEmphasized = true
        return v
    }

    func updateNSView(_ nsView: NSVisualEffectView, context: Context) {
        nsView.material = material
        nsView.blendingMode = .behindWindow
        nsView.state = .active
    }
}

struct MainTranslationView: View {
    @ObservedObject var model: HUDViewModel
    @ObservedObject var translator: SystemTranslationCoordinator

    private let activeEmerald = Color(red: 0x4D / 255.0, green: 0xA8 / 255.0, blue: 0x92 / 255.0)
    /// 1行ヘッダーに全ピルを横並びできる境界幅（これ未満では自動的に2段ヘッダーへ折り返す）
    static let singleRowHeaderThreshold: CGFloat = 520.0

    var body: some View {
        GeometryReader { geo in
            let availableWidth = max(320.0, geo.size.width)
            let useTwoRowHeader = !model.isCollapsed && (availableWidth < Self.singleRowHeaderThreshold)

            VStack(alignment: .leading, spacing: 0) {
                if useTwoRowHeader {
                    compactTwoRowHeader
                } else {
                    singleRowHeader(compactCollapsed: model.isCollapsed && availableWidth < Self.singleRowHeaderThreshold)
                }

                if !model.isCollapsed {
                    Rectangle()
                        .fill(model.isPipelineBusy ? activeEmerald.opacity(0.85) : Color.white.opacity(0.10))
                        .frame(width: availableWidth, height: 1.0)

                    ScrollView(.vertical, showsIndicators: true) {
                        VStack(alignment: .leading, spacing: 8) {
                            if !model.clusterCards.isEmpty {
                                let showMultiCardStyle = model.clusterCards.count >= 2
                                ForEach(model.clusterCards) { card in
                                    ClusterCardRowView(
                                        card: card,
                                        showsCardChrome: showMultiCardStyle,
                                        accentColor: activeEmerald
                                    ) { isHovering in
                                        model.onHoverClusterCard?(isHovering ? card : nil)
                                    }
                                }
                            } else if !model.streamingTranslatedText.isEmpty || !model.translatedText.isEmpty {
                                let upperHistory = !model.streamingTranslatedText.isEmpty && !model.translatedText.isEmpty
                                    ? model.translatedText
                                    : model.previousTranslatedText

                                if !upperHistory.isEmpty {
                                    Text(upperHistory)
                                        .font(.system(size: 13, weight: .regular))
                                        .foregroundStyle(Color.white.opacity(0.45))
                                        .lineSpacing(3)
                                        .fixedSize(horizontal: false, vertical: true)
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                }

                                if !model.streamingTranslatedText.isEmpty {
                                    Text(model.streamingTranslatedText)
                                        .font(.system(size: 15.5, weight: .medium))
                                        .foregroundStyle(Color.white.opacity(0.82))
                                        .lineSpacing(4.5)
                                        .fixedSize(horizontal: false, vertical: true)
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                } else {
                                    Text(model.translatedText)
                                        .font(.system(size: 15.5, weight: .medium))
                                        .foregroundStyle(Color.white.opacity(0.96))
                                        .lineSpacing(4.5)
                                        .fixedSize(horizontal: false, vertical: true)
                                        .textSelection(.enabled)
                                        .frame(maxWidth: .infinity, alignment: .leading)
                                }
                            } else {
                                Text(model.statusNotice)
                                    .font(.system(size: 13.5, weight: .regular))
                                    .foregroundStyle(model.isPipelineBusy ? Color.white.opacity(0.76) : Color.white.opacity(0.52))
                                    .lineSpacing(4)
                                    .fixedSize(horizontal: false, vertical: true)
                                    .frame(maxWidth: .infinity, alignment: .leading)
                            }
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 11)
                        .frame(width: availableWidth, alignment: .topLeading)
                    }
                    .frame(width: availableWidth)
                    .frame(maxHeight: .infinity, alignment: .topLeading)
                }
            }
            .frame(width: availableWidth, height: geo.size.height, alignment: .topLeading)
        }
        .ignoresSafeArea()
        .background(
            ZStack {
                AppleBackdropBlurView(material: .hudWindow)
                LinearGradient(
                    colors: [
                        Color.white.opacity(0.09),
                        Color.white.opacity(0.02)
                    ],
                    startPoint: .topLeading,
                    endPoint: .bottomTrailing
                )
            }
            .ignoresSafeArea()
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(
                    LinearGradient(
                        colors: [
                            Color.white.opacity(0.28),
                            Color.white.opacity(0.08)
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    ),
                    lineWidth: 0.6
                )
                .allowsHitTesting(false)
        )
        .translationTask(translator.configuration) { session in
            await translator.handleViewTranslationSession(session)
        }
    }

    // MARK: - Responsive Headers

    /// 横幅が広い場合（>= 520pt）または折りたたみ時の1段ヘッダー
    @ViewBuilder
    private func singleRowHeader(compactCollapsed: Bool) -> some View {
        HStack(spacing: 5) {
            closeButton
                .padding(.trailing, 1)

            languagePairPills

            if !compactCollapsed {
                backendPill
                displayModePill
            }

            stageStatusLabel

            Spacer(minLength: 4)

            rightActionButtons
        }
        .padding(.horizontal, 12)
        .frame(height: MainTranslationWindow.collapsedHeight)
    }

    /// 横幅が狭い場合（360〜519pt）に左右見切れをゼロにする2段レスポンシブヘッダー
    private var compactTwoRowHeader: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(spacing: 5) {
                closeButton
                    .padding(.trailing, 1)
                languagePairPills
                Spacer(minLength: 4)
                rightActionButtons
            }
            HStack(spacing: 6) {
                backendPill
                displayModePill
                stageStatusLabel
                Spacer(minLength: 0)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 7)
    }

    // MARK: - Header Building Blocks

    private var closeButton: some View {
        HeaderCircleButton(
            symbol: "xmark",
            helpText: "終了 (⌘Q / ⌥⇧Q)"
        ) {
            model.onCloseApp?()
        }
    }

    private var languagePairPills: some View {
        HStack(spacing: 4) {
            LanguageMenuPill(
                selected: model.sourceLanguage,
                options: TranslationLanguage.sourceOptions
            ) { chosen in
                guard chosen != model.sourceLanguage else { return }
                model.sourceLanguage = chosen
                model.onLanguageChanged?()
            }

            Image(systemName: "arrow.right")
                .font(.system(size: 9.0, weight: .semibold))
                .foregroundStyle(Color.white.opacity(0.38))

            LanguageMenuPill(
                selected: model.targetLanguage,
                options: TranslationLanguage.targetOptions
            ) { chosen in
                guard chosen != model.targetLanguage else { return }
                model.targetLanguage = chosen
                model.onLanguageChanged?()
            }
        }
    }

    private var backendPill: some View {
        BackendMenuPill(selected: model.backend) { chosen in
            guard chosen != model.backend else { return }
            model.backend = chosen
            model.onBackendChanged?()
        }
    }

    private var displayModePill: some View {
        DisplayModeMenuPill(selected: model.displayMode) { chosen in
            guard chosen != model.displayMode else { return }
            model.displayMode = chosen
            model.onDisplayModeChanged?()
        }
    }

    @ViewBuilder
    private var stageStatusLabel: some View {
        if !model.pipelineStageLabel.isEmpty {
            HStack(spacing: 4) {
                if model.isPipelineBusy {
                    Circle()
                        .fill(activeEmerald)
                        .frame(width: 5.5, height: 5.5)
                }
                Text(model.pipelineStageLabel)
                    .font(.system(size: 10.5, weight: .regular))
                    .foregroundStyle(
                        model.isPipelineBusy
                            ? Color.white.opacity(0.84)
                            : Color.white.opacity(0.46)
                    )
                    .lineLimit(1)
                    .truncationMode(.tail)
            }
            .layoutPriority(-1)
        }
    }

    private var rightActionButtons: some View {
        HStack(spacing: 4) {
            HeaderIconButton(
                symbol: "viewfinder",
                isProminent: false,
                helpText: "監視範囲を選択 (⌥⇧R)"
            ) {
                model.onSelectRegion?()
            }

            HeaderIconButton(
                symbol: model.isCapturing ? "stop.fill" : "play.fill",
                isProminent: model.isCapturing,
                helpText: model.isCapturing ? "監視を停止 (⌥⇧T)" : "監視を開始 (⌥⇧T)"
            ) {
                model.onToggleCapture?()
            }

            if model.canSelfRebuildFromSource {
                HeaderIconButton(
                    symbol: "arrow.triangle.2.circlepath",
                    isProminent: model.isUpdatingApp,
                    helpText: "アプリを再ビルドして再起動 (⌘⇧U)"
                ) {
                    guard !model.isUpdatingApp else { return }
                    model.onUpdateAndRelaunch?()
                }
                .disabled(model.isUpdatingApp)
            }

            HeaderIconButton(
                symbol: model.isCollapsed ? "chevron.down" : "chevron.up",
                isProminent: false,
                helpText: model.isCollapsed ? "ウィンドウを広げる (⌘K)" : "バーに折りたたむ (⌘K)"
            ) {
                model.onToggleCollapse?()
            }
        }
    }
}

/// まとまり（クラスタ）単位の翻訳カード行（ホバーすると画面側の該当座標をハイライト）
private struct ClusterCardRowView: View {
    let card: ClusterCardItem
    let showsCardChrome: Bool
    let accentColor: Color
    let onHoverChanged: (Bool) -> Void

    @State private var isHovered: Bool = false

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            if showsCardChrome {
                Text(card.badgeLabel)
                    .font(.system(size: 10, weight: .bold, design: .monospaced))
                    .foregroundStyle(isHovered || card.isTranslating ? accentColor : Color.white.opacity(0.42))
                    .padding(.top, 3)
            }

            Group {
                if !card.displayBodyText.isEmpty {
                    Text(card.displayBodyText)
                        .font(.system(size: 15.0, weight: .medium))
                        .foregroundStyle(Color.white.opacity(card.isTranslating ? 0.84 : 0.96))
                        .lineSpacing(4.2)
                        .fixedSize(horizontal: false, vertical: true)
                        .textSelection(.enabled)
                } else {
                    Text("翻訳中…")
                        .font(.system(size: 13.0, weight: .regular))
                        .foregroundStyle(Color.white.opacity(0.45))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, showsCardChrome ? 10 : 0)
        .padding(.vertical, showsCardChrome ? 8 : 2)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            Group {
                if showsCardChrome {
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(Color.white.opacity(isHovered ? 0.09 : 0.045))
                }
            }
        )
        .overlay(
            Group {
                if showsCardChrome {
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(
                            isHovered || card.isTranslating
                                ? accentColor.opacity(isHovered ? 0.65 : 0.45)
                                : Color.white.opacity(0.09),
                            lineWidth: 0.75
                        )
                }
            }
        )
        .onHover { hovering in
            isHovered = hovering
            onHoverChanged(hovering)
        }
    }
}

private struct LanguageMenuPill: View {
    let selected: TranslationLanguage
    let allOptions: [TranslationLanguage]
    let onSelect: (TranslationLanguage) -> Void

    init(selected: TranslationLanguage, options: [TranslationLanguage], onSelect: @escaping (TranslationLanguage) -> Void) {
        self.selected = selected
        self.allOptions = options
        self.onSelect = onSelect
    }

    var body: some View {
        Menu {
            ForEach(allOptions) { lang in
                Button {
                    onSelect(lang)
                } label: {
                    if lang == selected {
                        Label(lang.shortName, systemImage: "checkmark")
                    } else {
                        Text(lang.shortName)
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Text(selected.shortName)
                    .font(.system(size: 11.0, weight: .medium))
                    .lineLimit(1)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 8.0, weight: .semibold))
                    .opacity(0.60)
            }
            .foregroundStyle(Color.white.opacity(0.88))
            .padding(.horizontal, 6.5)
            .frame(height: 22)
            .background(Capsule().fill(Color.white.opacity(0.11)))
        }
        .menuStyle(.borderlessButton)
        .fixedSize(horizontal: true, vertical: false)
    }
}

private struct BackendMenuPill: View {
    let selected: TranslationBackendKind
    let onSelect: (TranslationBackendKind) -> Void

    var body: some View {
        Menu {
            ForEach(TranslationBackendKind.allCases) { kind in
                Button {
                    onSelect(kind)
                } label: {
                    if kind == selected {
                        Label(kind.menuTitle, systemImage: "checkmark")
                    } else {
                        Text(kind.menuTitle)
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Text(selected.shortName)
                    .font(.system(size: 10.5, weight: .medium))
                    .lineLimit(1)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 8.0, weight: .semibold))
                    .opacity(0.55)
            }
            .foregroundStyle(Color.white.opacity(0.76))
            .padding(.horizontal, 6.5)
            .frame(height: 20)
            .background(Capsule().fill(Color.white.opacity(0.08)))
        }
        .menuStyle(.borderlessButton)
        .fixedSize(horizontal: true, vertical: false)
        .help("翻訳エンジンを選択（PLaMo 3 Translate / OS標準）")
    }
}

private struct DisplayModeMenuPill: View {
    let selected: TranslationDisplayMode
    let onSelect: (TranslationDisplayMode) -> Void

    var body: some View {
        Menu {
            ForEach(TranslationDisplayMode.allCases) { mode in
                Button {
                    onSelect(mode)
                } label: {
                    if mode == selected {
                        Label(mode.menuTitle, systemImage: "checkmark")
                    } else {
                        Text(mode.menuTitle)
                    }
                }
            }
        } label: {
            HStack(spacing: 3) {
                Text(selected.shortName)
                    .font(.system(size: 10.5, weight: .medium))
                    .lineLimit(1)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 8.0, weight: .semibold))
                    .opacity(0.55)
            }
            .foregroundStyle(Color.white.opacity(0.78))
            .padding(.horizontal, 6.5)
            .frame(height: 20)
            .background(Capsule().fill(Color.white.opacity(0.08)))
        }
        .menuStyle(.borderlessButton)
        .fixedSize(horizontal: true, vertical: false)
        .help("表示モード切替: カード / 直接重畳 / 近傍のみ (⌥⇧M)")
    }
}

private struct HeaderCircleButton: View {
    let symbol: String
    let helpText: String
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 10, weight: .bold))
                .foregroundStyle(Color.white.opacity(hovered ? 0.95 : 0.68))
                .frame(width: 22, height: 22)
                .background(
                    Circle()
                        .fill(Color.white.opacity(hovered ? 0.18 : 0.10))
                )
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .help(helpText)
        .onHover { hovered = $0 }
    }
}

private struct HeaderIconButton: View {
    let symbol: String
    let isProminent: Bool
    let helpText: String
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(Color.white.opacity(isProminent ? 0.98 : (hovered ? 0.94 : 0.78)))
                .frame(width: 24, height: 22)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(Color.white.opacity(isProminent ? 0.24 : (hovered ? 0.16 : 0.10)))
                )
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(helpText)
        .onHover { hovered = $0 }
    }
}

/// fullSizeContentView + titled ウィンドウで macOS が自動注入する上部 28pt のタイトルバー SafeArea 余白を無効化し、
/// かつ SwiftUI の intrinsicSize が NSWindow のリサイズを阻害しないようにするカスタム HostingView
final class FirstMouseHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool {
        true
    }

    override var safeAreaInsets: NSEdgeInsets {
        NSEdgeInsets(top: 0, left: 0, bottom: 0, right: 0)
    }
}

/// macOS のウィンドウシステムに正式認識されつつ、カラフルな信号機ボタンを非表示にした標準 NSWindow
final class MainTranslationWindow: NSWindow {
    static let collapsedHeight: CGFloat = 38.0
    /// スクリーンショットと同等のコンパクト幅（360pt）を最小幅として設定
    static let minExpandedSize = NSSize(width: 360.0, height: 145.0)
    static let maxExpandedSize = NSSize(width: 920.0, height: 680.0)
    static let defaultExpandedSize = NSSize(width: 460.0, height: 220.0)

    let viewModel = HUDViewModel()
    private(set) var hostingView: FirstMouseHostingView<MainTranslationView>!
    private(set) var savedExpandedHeight: CGFloat = MainTranslationWindow.defaultExpandedSize.height

    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }

    init(translator: SystemTranslationCoordinator) {
        let initialW = Self.clampWidth(CGFloat(UserDefaults.standard.double(forKey: "win.width")))
        let initialH = Self.clampHeight(CGFloat(UserDefaults.standard.double(forKey: "win.height")))
        self.savedExpandedHeight = initialH

        let rect = NSRect(x: 160, y: 140, width: initialW, height: initialH)
        super.init(
            contentRect: rect,
            styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )

        title = "ScreenTranslate"
        titleVisibility = .hidden
        titlebarAppearsTransparent = true
        appearance = NSAppearance(named: .vibrantDark)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        isMovableByWindowBackground = true
        isReleasedWhenClosed = false
        level = .floating
        collectionBehavior = [.fullScreenAuxiliary]

        hideTrafficLights()

        minSize = Self.minExpandedSize
        maxSize = Self.maxExpandedSize

        hostingView = FirstMouseHostingView(rootView: MainTranslationView(model: viewModel, translator: translator))
        hostingView.sizingOptions = []
        hostingView.translatesAutoresizingMaskIntoConstraints = true
        hostingView.autoresizingMask = [.width, .height]
        contentView = hostingView

        restoreWindowPosition(width: initialW, height: initialH)
        hideTrafficLights()
    }

    func hideTrafficLights() {
        standardWindowButton(.closeButton)?.isHidden = true
        standardWindowButton(.miniaturizeButton)?.isHidden = true
        standardWindowButton(.zoomButton)?.isHidden = true
    }

    var areTrafficLightsHidden: Bool {
        let c = standardWindowButton(.closeButton)?.isHidden ?? true
        let m = standardWindowButton(.miniaturizeButton)?.isHidden ?? true
        let z = standardWindowButton(.zoomButton)?.isHidden ?? true
        return c && m && z
    }

    override func makeKeyAndOrderFront(_ sender: Any?) {
        super.makeKeyAndOrderFront(sender)
        hideTrafficLights()
    }

    private static func clampWidth(_ raw: CGFloat) -> CGFloat {
        guard raw >= minExpandedSize.width else { return defaultExpandedSize.width }
        return min(max(raw, minExpandedSize.width), maxExpandedSize.width)
    }

    private static func clampHeight(_ raw: CGFloat) -> CGFloat {
        guard raw >= minExpandedSize.height else { return defaultExpandedSize.height }
        return min(max(raw, minExpandedSize.height), maxExpandedSize.height)
    }

    func toggleCollapse(animated: Bool = true) {
        let currentFrame = frame
        let topY = currentFrame.maxY

        if !viewModel.isCollapsed {
            if currentFrame.height >= Self.minExpandedSize.height {
                savedExpandedHeight = currentFrame.height
                UserDefaults.standard.set(Double(savedExpandedHeight), forKey: "win.height")
            }
            viewModel.isCollapsed = true
            minSize = NSSize(width: Self.minExpandedSize.width, height: Self.collapsedHeight)
            maxSize = NSSize(width: Self.maxExpandedSize.width, height: Self.collapsedHeight)

            let collapsedFrame = NSRect(
                x: currentFrame.minX,
                y: topY - Self.collapsedHeight,
                width: currentFrame.width,
                height: Self.collapsedHeight
            )
            setFrame(collapsedFrame, display: true, animate: animated)
        } else {
            viewModel.isCollapsed = false
            let restoreH = Self.clampHeight(savedExpandedHeight)
            minSize = Self.minExpandedSize
            maxSize = Self.maxExpandedSize

            let expandedFrame = NSRect(
                x: currentFrame.minX,
                y: topY - restoreH,
                width: currentFrame.width,
                height: restoreH
            )
            setFrame(expandedFrame, display: true, animate: animated)
        }
        hideTrafficLights()
        saveWindowGeometry()
    }

    func hasActiveVisualEffectBlur() -> Bool {
        guard let root = contentView else { return false }
        root.layoutSubtreeIfNeeded()
        func search(_ v: NSView) -> Bool {
            if let vev = v as? NSVisualEffectView {
                return vev.blendingMode == .behindWindow && vev.state == .active
            }
            return v.subviews.contains(where: search)
        }
        return search(root)
    }

    override func setFrame(_ frameRect: NSRect, display flag: Bool) {
        super.setFrame(frameRect, display: flag)
        hideTrafficLights()
        if !viewModel.isCollapsed && frameRect.height >= Self.minExpandedSize.height {
            savedExpandedHeight = frameRect.height
        }
    }

    private func restoreWindowPosition(width: CGFloat, height: CGFloat) {
        if UserDefaults.standard.object(forKey: "win.topLeftX") != nil {
            let x = UserDefaults.standard.double(forKey: "win.topLeftX")
            let topY = UserDefaults.standard.double(forKey: "win.topLeftY")
            setFrame(NSRect(x: x, y: topY - height, width: width, height: height), display: true)
            return
        }
        if let visible = NSScreen.main?.visibleFrame {
            let x = visible.midX - width * 0.5
            let y = visible.minY + 96.0
            setFrame(NSRect(x: x, y: y, width: width, height: height), display: true)
        }
    }

    func saveWindowGeometry() {
        UserDefaults.standard.set(Double(frame.minX), forKey: "win.topLeftX")
        UserDefaults.standard.set(Double(frame.maxY), forKey: "win.topLeftY")
        UserDefaults.standard.set(Double(frame.width), forKey: "win.width")
        if !viewModel.isCollapsed && frame.height >= Self.minExpandedSize.height {
            UserDefaults.standard.set(Double(frame.height), forKey: "win.height")
        }
    }
}
