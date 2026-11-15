import AppKit
#if canImport(ScreenTranslateCore)
import ScreenTranslateCore
#endif

@MainActor
final class AppController: NSObject, NSApplicationDelegate, NSWindowDelegate {
    enum TestMode {
        case selfTestUI
        case selfTestDaemon
    }

    private static let terminateNotification = Notification.Name("com.corunya.screentranslate.terminateOldInstances")

    let translator = SystemTranslationCoordinator()
    private(set) lazy var mainWindow = MainTranslationWindow(translator: translator)
    private let regionSelector = RegionSelectorController()
    private let regionIndicator = RegionIndicatorPanel()
    private let inPlaceOverlay = InPlaceOverlayController()
    private let captureEngine = CaptureEngine()
    private let hotKeys = HotKeyManager()

    private var currentSelection: CaptureSelection?
    private var showsRegionCorners: Bool = false
    private var hoveredClusterCard: ClusterCardItem?

    /// 直近フレームでOCR検出された有効クラスタ群（表示モード切替やスポットモードのカーソル追従で即時再利用）
    private var latestRawClusters: [ExtractedTextCluster] = []
    /// クラスタ単位の既訳履歴（スクロール移動時は LLM を呼ばず 0ms で訳文を引き継ぐ）
    private var cachedClusterEntries: [ClusterTranslationPlanner.CachedEntry] = []
    private var activeTranslateTask: Task<Void, Never>?
    private var activeUnresolvedKey: String = ""
    private var batchGeneration: UInt64 = 0
    private var spotCursorTimer: Timer?
    private var lastSpotSelectedCore: String = ""

    private let instanceID = UUID().uuidString

    private var controlFileDescriptor: Int32 = -1
    private var controlSource: DispatchSourceFileSystemObject?
    private var runtimeDirURL: URL?
    private var projectRootURL: URL?

    private var floatMenuItem: NSMenuItem?
    private var cornersMenuItem: NSMenuItem?
    private var engineMenuItems: [TranslationBackendKind: NSMenuItem] = [:]
    private var displayModeMenuItems: [TranslationDisplayMode: NSMenuItem] = [:]

    // MARK: - Lifecycle

    func startForTesting(mode: TestMode) {
        setupRuntimeControlChannel()
        mainWindow.delegate = self
        wireWindowActions()
        wireCaptureCallbacks()
        _ = mainWindow.contentView
        mainWindow.contentView?.layoutSubtreeIfNeeded()

        if mode == .selfTestUI {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
                self?.runAutomatedUISelfTest()
            }
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupSingleInstanceReplacement()
        setupRuntimeControlChannel()

        NSApp.setActivationPolicy(.regular)
        setupStandardMainMenu()

        mainWindow.delegate = self
        wireWindowActions()
        wireCaptureCallbacks()
        wireHotKeys()

        mainWindow.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        ensureSelectedBackendRunning()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag {
            mainWindow.makeKeyAndOrderFront(nil)
        }
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        mainWindow.saveWindowGeometry()
        cleanupResources()
    }

    func windowWillClose(_ notification: Notification) {
        if (notification.object as? NSWindow) === mainWindow {
            performCleanExit(code: 0)
        }
    }

    func windowDidResize(_ notification: Notification) {
        mainWindow.saveWindowGeometry()
    }

    func windowDidMove(_ notification: Notification) {
        mainWindow.saveWindowGeometry()
    }

    private func cleanupResources(stopSidecar: Bool = true) {
        spotCursorTimer?.invalidate()
        spotCursorTimer = nil
        inPlaceOverlay.hide()
        hotKeys.unregisterAll()
        controlSource?.cancel()
        controlSource = nil
        if controlFileDescriptor >= 0 {
            Darwin.close(controlFileDescriptor)
            controlFileDescriptor = -1
        }
        if let pidFile = runtimeDirURL?.appendingPathComponent("pid") {
            try? FileManager.default.removeItem(at: pidFile)
        }
        if stopSidecar {
            stopPLaMoSidecarIfOwned()
        }
        DistributedNotificationCenter.default().removeObserver(self)
    }

    private func resolveManagePlamoScriptURL() -> URL? {
        if let root = projectRootURL {
            let devScript = root.appendingPathComponent("scripts/manage_plamo.sh")
            if FileManager.default.fileExists(atPath: devScript.path) {
                return devScript
            }
        }
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("manage_plamo.sh"),
           FileManager.default.fileExists(atPath: bundled.path) {
            return bundled
        }
        return nil
    }

    private func runManagePlamoScript(command: String, extraArg: String? = nil) {
        guard let scriptURL = resolveManagePlamoScriptURL(),
              let rtDir = runtimeDirURL else { return }
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/bash")
        proc.arguments = extraArg.map { [scriptURL.path, command, $0] } ?? [scriptURL.path, command]
        proc.currentDirectoryURL = projectRootURL ?? rtDir
        var env = ProcessInfo.processInfo.environment
        env["SCREEN_TRANSLATE_RUNTIME_DIR"] = rtDir.path
        proc.environment = env
        proc.standardOutput = FileHandle.nullDevice
        proc.standardError = FileHandle.nullDevice
        try? proc.run()
    }

    private func stopPLaMoSidecarIfOwned() {
        let args = CommandLine.arguments
        guard !args.contains("--self-test-ui") && !args.contains("--self-test-daemon") else { return }
        runManagePlamoScript(command: "stop")
    }

    private func performCleanExit(code: Int32 = 0, stopSidecar: Bool = true) -> Never {
        mainWindow.saveWindowGeometry()
        mainWindow.orderOut(nil)
        regionIndicator.orderOut(nil)
        inPlaceOverlay.hide()
        cleanupResources(stopSidecar: stopSidecar)
        fflush(stdout)
        fflush(stderr)
        Darwin.exit(code)
    }

    // MARK: - Standard macOS App Menu Bar

    private func setupStandardMainMenu() {
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        mainMenu.addItem(appMenuItem)
        let appMenu = NSMenu()
        if projectRootURL != nil {
            let updateItem = NSMenuItem(title: "アップデートして再起動…", action: #selector(updateAndRelaunchAction), keyEquivalent: "u")
            updateItem.keyEquivalentModifierMask = [.command, .shift]
            updateItem.target = self
            appMenu.addItem(updateItem)
            appMenu.addItem(.separator())
        }
        appMenu.addItem(withTitle: "ScreenTranslate を隠す", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let hideOthers = appMenu.addItem(withTitle: "ほかを隠す", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        hideOthers.keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(.separator())
        let quitItem = NSMenuItem(title: "ScreenTranslate を終了", action: #selector(quitAction), keyEquivalent: "q")
        quitItem.target = self
        appMenu.addItem(quitItem)
        appMenuItem.submenu = appMenu

        let editMenuItem = NSMenuItem()
        mainMenu.addItem(editMenuItem)
        let editMenu = NSMenu(title: "編集")
        editMenu.addItem(withTitle: "コピー", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "すべてを選択", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editMenuItem.submenu = editMenu

        let engineRootItem = NSMenuItem()
        mainMenu.addItem(engineRootItem)
        let engineMenu = NSMenu(title: "翻訳エンジン")
        for kind in TranslationBackendKind.allCases {
            let item = NSMenuItem(title: kind.menuTitle, action: #selector(selectBackendMenuAction(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = kind.rawValue
            item.state = (mainWindow.viewModel.backend == kind) ? .on : .off
            engineMenu.addItem(item)
            engineMenuItems[kind] = item
        }
        engineRootItem.submenu = engineMenu

        let winMenuItem = NSMenuItem()
        mainMenu.addItem(winMenuItem)
        let winMenu = NSMenu(title: "ウィンドウ")
        let closeWinItem = NSMenuItem(title: "閉じる", action: #selector(quitAction), keyEquivalent: "w")
        closeWinItem.target = self
        winMenu.addItem(closeWinItem)
        winMenu.addItem(withTitle: "しまう", action: #selector(NSWindow.miniaturize(_:)), keyEquivalent: "m")
        let collapseItem = NSMenuItem(title: "折りたたむ / 広げる", action: #selector(toggleCollapseAction), keyEquivalent: "k")
        collapseItem.target = self
        winMenu.addItem(collapseItem)
        winMenu.addItem(.separator())

        for mode in TranslationDisplayMode.allCases {
            let mItem = NSMenuItem(title: mode.menuTitle, action: #selector(selectDisplayModeMenuAction(_:)), keyEquivalent: "")
            mItem.target = self
            mItem.representedObject = mode.rawValue
            mItem.state = (mainWindow.viewModel.displayMode == mode) ? .on : .off
            winMenu.addItem(mItem)
            displayModeMenuItems[mode] = mItem
        }
        winMenu.addItem(.separator())

        let floatItem = NSMenuItem(title: "常に最前面に表示", action: #selector(toggleFloatingAction), keyEquivalent: "")
        floatItem.target = self
        floatItem.state = (mainWindow.level == .floating) ? .on : .off
        winMenu.addItem(floatItem)
        self.floatMenuItem = floatItem

        let cornersItem = NSMenuItem(title: "監視範囲の四隅枠を常時表示", action: #selector(toggleCornersAction), keyEquivalent: "")
        cornersItem.target = self
        cornersItem.state = .off
        winMenu.addItem(cornersItem)
        self.cornersMenuItem = cornersItem

        winMenuItem.submenu = winMenu
        NSApp.mainMenu = mainMenu
    }

    @objc private func selectBackendMenuAction(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let kind = TranslationBackendKind(rawValue: raw),
              kind != mainWindow.viewModel.backend else { return }
        mainWindow.viewModel.backend = kind
        handleBackendSelectionChanged()
    }

    @objc private func selectDisplayModeMenuAction(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String,
              let mode = TranslationDisplayMode(rawValue: raw),
              mode != mainWindow.viewModel.displayMode else { return }
        mainWindow.viewModel.displayMode = mode
        handleDisplayModeChanged()
    }

    private func syncEngineMenuCheckmarks() {
        let current = mainWindow.viewModel.backend
        for (kind, item) in engineMenuItems {
            item.state = (kind == current) ? .on : .off
        }
    }

    private func syncDisplayModeMenuCheckmarks() {
        let current = mainWindow.viewModel.displayMode
        for (mode, item) in displayModeMenuItems {
            item.state = (mode == current) ? .on : .off
        }
    }

    // MARK: - "./close" 連携 kqueue 制御チャネル

    private func resolveRuntimeDirectory() -> URL {
        var dir = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent()
        for _ in 0..<6 {
            if FileManager.default.fileExists(atPath: dir.appendingPathComponent("Package.swift").path) {
                self.projectRootURL = dir
                break
            }
            dir = dir.deletingLastPathComponent()
        }
        mainWindow.viewModel.canSelfRebuildFromSource = (self.projectRootURL != nil)

        if let envDir = ProcessInfo.processInfo.environment["SCREEN_TRANSLATE_RUNTIME_DIR"], !envDir.isEmpty {
            return URL(fileURLWithPath: envDir)
        }
        if let root = projectRootURL {
            return root.appendingPathComponent(".runtime")
        }
        if let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            return appSupport.appendingPathComponent("ScreenTranslate")
        }
        return URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("screentranslate-runtime")
    }

    private func setupRuntimeControlChannel() {
        let dir = resolveRuntimeDirectory()
        self.runtimeDirURL = dir
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let cmdURL = dir.appendingPathComponent("control.cmd")
        try? "".write(to: cmdURL, atomically: true, encoding: .utf8)

        let fd = Darwin.open(cmdURL.path, O_EVTONLY)
        if fd >= 0 {
            self.controlFileDescriptor = fd
            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: fd,
                eventMask: [.write, .extend, .attrib, .rename],
                queue: .main
            )
            source.setEventHandler { [weak self] in
                guard let self else { return }
                guard let content = try? String(contentsOf: cmdURL, encoding: .utf8) else { return }
                let cmd = content.trimmingCharacters(in: .whitespacesAndNewlines)
                if cmd == "quit" || cmd == "close" {
                    try? "".write(to: cmdURL, atomically: true, encoding: .utf8)
                    self.performCleanExit(code: 0)
                }
            }
            source.resume()
            self.controlSource = source
        }

        let pidURL = dir.appendingPathComponent("pid")
        try? "\(ProcessInfo.processInfo.processIdentifier)\n".write(to: pidURL, atomically: true, encoding: .utf8)
    }

    private func setupSingleInstanceReplacement() {
        DistributedNotificationCenter.default().postNotificationName(
            Self.terminateNotification,
            object: instanceID,
            userInfo: nil,
            deliverImmediately: true
        )
        DistributedNotificationCenter.default().addObserver(
            self,
            selector: #selector(handleTerminateBroadcast(_:)),
            name: Self.terminateNotification,
            object: nil
        )
    }

    @objc private func handleTerminateBroadcast(_ note: Notification) {
        if let senderID = note.object as? String, senderID != instanceID {
            performCleanExit(code: 0)
        }
    }

    // MARK: - PLaMo Sidecar Launcher

    private var lastLaunchedBackend: TranslationBackendKind?

    private func ensureSelectedBackendRunning(force: Bool = false) {
        let vm = mainWindow.viewModel
        syncEngineMenuCheckmarks()
        translator.resetModelCache()

        if !force, lastLaunchedBackend == vm.backend {
            return
        }
        lastLaunchedBackend = vm.backend

        if let sizeArg = vm.backend.plamoSizeArg {
            runManagePlamoScript(command: "start", extraArg: sizeArg)
        } else {
            translator.preparePairIfConcrete(
                source: vm.sourceLanguage,
                target: vm.targetLanguage,
                backend: vm.backend
            )
        }
    }

    // MARK: - Automated UI, Cluster Card, In-Place & Spot Self-Test (--self-test-ui)

    private func runAutomatedUISelfTest() {
        var failures: [String] = []

        let mask = mainWindow.styleMask
        if !mask.contains(.titled) || !mask.contains(.closable) || !mask.contains(.miniaturizable) || !mask.contains(.resizable) {
            failures.append("Window must support titled/closable/miniaturizable/resizable standard macOS behaviors")
        }
        if !mainWindow.areTrafficLightsHidden {
            failures.append("Traffic light buttons should be hidden")
        }
        if mainWindow.viewModel.onCloseApp == nil {
            failures.append("Monochrome close button callback not wired")
        }
        if mainWindow.viewModel.onUpdateAndRelaunch == nil {
            failures.append("One-click update button callback not wired")
        }
        if mainWindow.viewModel.onDisplayModeChanged == nil {
            failures.append("Display mode selector callback not wired")
        }
        if !mainWindow.hasActiveVisualEffectBlur() {
            failures.append("NSVisualEffectView backdrop blur missing")
        }

        mainWindow.setFrame(NSRect(x: 160, y: 140, width: 560, height: 240), display: true)
        if abs(mainWindow.frame.width - 560) > 1 || abs(mainWindow.frame.height - 240) > 1 {
            failures.append("Resizable window failed to resize to 560x240 (got \(mainWindow.frame.size))")
        }

        let topBefore = mainWindow.frame.maxY
        mainWindow.toggleCollapse(animated: false)
        if !mainWindow.viewModel.isCollapsed || abs(mainWindow.frame.height - MainTranslationWindow.collapsedHeight) > 1 {
            failures.append("Collapse failed (height=\(mainWindow.frame.height))")
        }
        if abs(mainWindow.frame.maxY - topBefore) > 1 {
            failures.append("Collapse shifted top edge (\(mainWindow.frame.maxY) vs \(topBefore))")
        }
        mainWindow.toggleCollapse(animated: false)
        if mainWindow.viewModel.isCollapsed || abs(mainWindow.frame.height - 240) > 1 {
            failures.append("Uncollapse failed to restore height 240 (got \(mainWindow.frame.height))")
        }

        // クラスタ別カード・既訳0ms引き継ぎ・スキップ保護の検証
        let vm = mainWindow.viewModel
        vm.clearTexts()
        vm.isCapturing = true
        vm.displayMode = .cardList
        currentSelection = CaptureSelection(
            globalRect: CGRect(x: 100, y: 100, width: 600, height: 800),
            screenFrame: CGRect(x: 0, y: 0, width: 1440, height: 900),
            displayID: CGMainDisplayID()
        )

        recordCachedClusterTranslation(
            sourceText: "The sharp-tongued personal maid with no filter.",
            translatedText: "毒舌で遠慮のない専属メイド。"
        )
        recordCachedClusterTranslation(
            sourceText: "A nun deeply devoted to the sacred tree.",
            translatedText: "聖樹に深く帰依する修道女。"
        )

        let c1 = ExtractedTextCluster(
            text: "The sharp-tongued personal maid with no filter.",
            averageConfidence: 0.94,
            minX: 0.1, maxX: 0.8, topY: 0.10, bottomY: 0.22
        )
        let c2 = ExtractedTextCluster(
            text: "A nun deeply devoted to the sacred tree.",
            averageConfidence: 0.93,
            minX: 0.1, maxX: 0.8, topY: 0.60, bottomY: 0.72
        )

        handleCapturedText(.commitClusters(clusters: [c1, c2]))
        if vm.clusterCards.count != 2
            || vm.clusterCards[0].translatedText != "毒舌で遠慮のない専属メイド。"
            || vm.clusterCards[1].translatedText != "聖樹に深く帰依する修道女。"
            || vm.isPipelineBusy {
            failures.append("Zero-latency cached multi-cluster card rendering failed (count=\(vm.clusterCards.count))")
        }

        handleCapturedText(.skipped(reason: .numericOrStatsOnly))
        if vm.clusterCards.count != 2 || vm.pipelineStageLabel != "除外: 数値" {
            failures.append("Skip event wiped out existing cluster cards")
        }

        if !failures.isEmpty {
            for f in failures {
                fputs("UI_SELF_TEST_FAIL: \(f)\n", stderr)
            }
            performCleanExit(code: 1)
        }
        print("UI_SELF_TEST_PASSED: Multi-cluster cards, 0ms scroll cache, display modes, one-click update, resize, and collapse verified.")
        performCleanExit(code: 0)
    }

    // MARK: - Wiring

    private func wireWindowActions() {
        let vm = mainWindow.viewModel
        vm.onCloseApp = { [weak self] in self?.quitAction() }
        vm.onSelectRegion = { [weak self] in self?.selectRegionAction() }
        vm.onToggleCapture = { [weak self] in self?.toggleCaptureAction() }
        vm.onUpdateAndRelaunch = { [weak self] in self?.updateAndRelaunchAction() }
        vm.onToggleCollapse = { [weak self] in self?.toggleCollapseAction() }
        vm.onLanguageChanged = { [weak self] in self?.handleLanguageSelectionChanged() }
        vm.onBackendChanged = { [weak self] in self?.handleBackendSelectionChanged() }
        vm.onDisplayModeChanged = { [weak self] in self?.handleDisplayModeChanged() }
        vm.onHoverClusterCard = { [weak self] card in self?.handleHoverClusterCard(card) }
    }

    private func wireCaptureCallbacks() {
        captureEngine.onTextEvent = { [weak self] event in
            Task { @MainActor in
                self?.handleCapturedText(event)
            }
        }

        captureEngine.onStatusError = { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                self.syncRunningUI(isRunning: false)
                self.mainWindow.viewModel.statusNotice = "画面収録の権限が必要です（システム設定 → プライバシーとセキュリティ → 画面収録）"
            }
        }
    }

    private func wireHotKeys() {
        hotKeys.onAction = { [weak self] action in
            Task { @MainActor in
                guard let self else { return }
                switch action {
                case .selectRegion:
                    self.selectRegionAction()
                case .toggleCapture:
                    self.toggleCaptureAction()
                case .toggleClickThrough:
                    self.toggleCollapseAction()
                case .toggleVisibility:
                    if self.mainWindow.isVisible {
                        self.mainWindow.orderOut(nil)
                    } else {
                        self.mainWindow.makeKeyAndOrderFront(nil)
                    }
                case .quitApp:
                    self.quitAction()
                case .cycleDisplayMode:
                    self.mainWindow.viewModel.displayMode = self.mainWindow.viewModel.displayMode.nextMode()
                    self.handleDisplayModeChanged()
                }
            }
        }
        hotKeys.registerDefaults()
    }

    // MARK: - Mode, Hover & Cache Management

    private func recordCachedClusterTranslation(sourceText: String, translatedText: String) {
        let entry = ClusterTranslationPlanner.CachedEntry(sourceText: sourceText, translatedText: translatedText)
        guard !entry.semanticCore.isEmpty, !translatedText.isEmpty else { return }
        cachedClusterEntries.removeAll { $0.semanticCore == entry.semanticCore }
        cachedClusterEntries.append(entry)
        if cachedClusterEntries.count > 96 {
            cachedClusterEntries.removeFirst(cachedClusterEntries.count - 96)
        }
    }

    private func handleHoverClusterCard(_ card: ClusterCardItem?) {
        hoveredClusterCard = card
        refreshRegionIndicator()
    }

    private func refreshRegionIndicator() {
        if let hovered = hoveredClusterCard, mainWindow.viewModel.isCapturing {
            regionIndicator.showAround(
                globalRect: hovered.globalRect,
                badgeText: (mainWindow.viewModel.clusterCards.count >= 2) ? hovered.badgeLabel : nil,
                emphasized: true
            )
        } else if showsRegionCorners, let sel = currentSelection, captureEngine.isRunning {
            regionIndicator.showAround(globalRect: sel.globalRect, badgeText: nil, emphasized: false)
        } else {
            regionIndicator.orderOut(nil)
        }
    }

    private func syncInPlaceOverlay() {
        let vm = mainWindow.viewModel
        guard vm.isCapturing,
              vm.displayMode == .inPlace,
              let sel = currentSelection else {
            inPlaceOverlay.hide()
            return
        }
        inPlaceOverlay.update(items: vm.clusterCards, screenFrame: sel.screenFrame)
    }

    private func updateSpotCursorPolling() {
        let vm = mainWindow.viewModel
        if vm.isCapturing && vm.displayMode == .cursorSpot {
            guard spotCursorTimer == nil else { return }
            spotCursorTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
                Task { @MainActor [weak self] in
                    self?.pollCursorSpotSelectionIfNeeded()
                }
            }
        } else {
            spotCursorTimer?.invalidate()
            spotCursorTimer = nil
            lastSpotSelectedCore = ""
        }
    }

    private func pollCursorSpotSelectionIfNeeded() {
        let vm = mainWindow.viewModel
        guard vm.isCapturing,
              vm.displayMode == .cursorSpot,
              !latestRawClusters.isEmpty,
              let sel = currentSelection else {
            return
        }
        let normCursor = ClusterGeometry.normalizedCursorPoint(
            globalMousePoint: NSEvent.mouseLocation,
            in: sel.globalRect
        )
        let chosen = ClusterTranslationPlanner.selectActiveClusters(
            from: latestRawClusters,
            mode: .cursorSpot,
            normalizedCursor: normCursor
        )
        guard let first = chosen.first, first.semanticCore != lastSpotSelectedCore else {
            return
        }
        lastSpotSelectedCore = first.semanticCore
        applyClustersAndScheduleTranslations(from: latestRawClusters)
    }

    private func handleDisplayModeChanged() {
        syncDisplayModeMenuCheckmarks()
        updateSpotCursorPolling()

        if !latestRawClusters.isEmpty {
            applyClustersAndScheduleTranslations(from: latestRawClusters)
        } else {
            syncInPlaceOverlay()
            captureEngine.invalidateFrameCache()
        }
    }

    private func handleLanguageSelectionChanged() {
        let vm = mainWindow.viewModel
        cancelActiveTranslationBatch()
        cachedClusterEntries.removeAll()
        latestRawClusters.removeAll()
        translator.clearCache()
        captureEngine.setTranslationBusy(false)
        captureEngine.updateLanguages(
            sourceCode: vm.sourceLanguage.code,
            targetCode: vm.targetLanguage.code
        )
        translator.preparePairIfConcrete(
            source: vm.sourceLanguage,
            target: vm.targetLanguage,
            backend: vm.backend
        )
    }

    private func handleBackendSelectionChanged() {
        let vm = mainWindow.viewModel
        cancelActiveTranslationBatch()
        cachedClusterEntries.removeAll()
        ensureSelectedBackendRunning(force: true)
        vm.updateStage("\(vm.backend.shortName) へ切替", busy: false)
        if !latestRawClusters.isEmpty {
            applyClustersAndScheduleTranslations(from: latestRawClusters)
        } else {
            captureEngine.updateLanguages(
                sourceCode: vm.sourceLanguage.code,
                targetCode: vm.targetLanguage.code
            )
        }
    }

    // MARK: - Cluster Pipeline & Cursor-Prioritized Translation Scheduler

    private func cancelActiveTranslationBatch() {
        batchGeneration &+= 1
        activeTranslateTask?.cancel()
        activeTranslateTask = nil
        activeUnresolvedKey = ""
        captureEngine.setTranslationBusy(false)
    }

    private func handleCapturedText(_ event: CapturedTextEvent) {
        let vm = mainWindow.viewModel

        switch event {
        case .ocrStarted:
            if activeTranslateTask == nil {
                vm.updateStage("OCR読取中…", busy: true)
            }
        case .provisional:
            if activeTranslateTask == nil {
                vm.updateStage("文字安定待ち…", busy: true)
            }
        case .idle:
            if activeTranslateTask == nil {
                vm.updateStage(vm.isCapturing ? "待機中" : "", busy: false)
            }
        case .skipped(let reason):
            if activeTranslateTask == nil {
                vm.presentSkipped(reason)
            }
        case .commitClusters(let clusters):
            latestRawClusters = clusters
            applyClustersAndScheduleTranslations(from: clusters)
        }
    }

    private func applyClustersAndScheduleTranslations(from rawCandidates: [ExtractedTextCluster]) {
        let vm = mainWindow.viewModel
        let captureRect = currentSelection?.globalRect ?? CGRect(x: 0, y: 0, width: 640, height: 480)
        let normCursor = ClusterGeometry.normalizedCursorPoint(
            globalMousePoint: NSEvent.mouseLocation,
            in: captureRect
        )

        let selected = ClusterTranslationPlanner.selectActiveClusters(
            from: rawCandidates,
            mode: vm.displayMode,
            normalizedCursor: normCursor
        )
        guard !selected.isEmpty else { return }

        if vm.displayMode == .cursorSpot, let first = selected.first {
            lastSpotSelectedCore = first.semanticCore
        }

        var cards: [ClusterCardItem] = []
        cards.reserveCapacity(selected.count)
        var unresolvedIndices: [Int] = []

        for (idx, cl) in selected.enumerated() {
            let gRect = ClusterGeometry.globalRect(for: cl, in: captureRect)
            let cachedHit = ClusterTranslationPlanner.findCachedTranslation(
                for: cl,
                in: cachedClusterEntries
            )
            if cachedHit == nil {
                unresolvedIndices.append(idx)
            }
            let stableID = "\(idx)-\(String(cl.semanticCore.prefix(24)))"
            cards.append(
                ClusterCardItem(
                    id: stableID,
                    indexNumber: idx + 1,
                    sourceText: cl.text,
                    globalRect: gRect,
                    translatedText: cachedHit ?? "",
                    streamingText: "",
                    isTranslating: (cachedHit == nil)
                )
            )
        }

        // 全クラスタが既訳キャッシュにヒットした場合（スクロール移動・再表示）は LLM 呼び出しゼロ(0ms)で即確定！
        if unresolvedIndices.isEmpty {
            cancelActiveTranslationBatch()
            vm.applyClusterCards(cards, busy: false, stage: vm.isCapturing ? "待機中" : "")
            syncInPlaceOverlay()
            refreshRegionIndicator()
            return
        }

        // マウスカーソルに近い未翻訳クラスタを先頭へ引き上げる
        let orderedUnresolved = ClusterTranslationPlanner.prioritizedIndices(
            clusters: selected,
            untranslatedIndices: unresolvedIndices,
            normalizedCursor: normCursor
        )

        let newUnresolvedKey = orderedUnresolved.map { selected[$0].semanticCore }.joined(separator: "||")
        if activeTranslateTask != nil && newUnresolvedKey == activeUnresolvedKey {
            // 同じ未翻訳クラスタ群を実行中であれば、スクロール等で動いた座標のみ即時反映して継続する
            var mergedCards = cards
            for idx in mergedCards.indices {
                if let runningMatch = vm.clusterCards.first(where: { $0.sourceText == mergedCards[idx].sourceText }) {
                    if mergedCards[idx].translatedText.isEmpty {
                        mergedCards[idx].translatedText = runningMatch.translatedText
                        mergedCards[idx].streamingText = runningMatch.streamingText
                        mergedCards[idx].isTranslating = runningMatch.isTranslating
                    }
                }
            }
            vm.clusterCards = mergedCards
            syncInPlaceOverlay()
            return
        }

        cancelActiveTranslationBatch()
        activeUnresolvedKey = newUnresolvedKey
        batchGeneration &+= 1
        let myGen = batchGeneration

        let srcLang = vm.sourceLanguage
        let tgtLang = vm.targetLanguage
        let backend = vm.backend
        let runtimeDir = runtimeDirURL

        vm.applyClusterCards(
            cards,
            busy: true,
            stage: "\(backend.shortName) 翻訳中 (1/\(orderedUnresolved.count))"
        )
        syncInPlaceOverlay()
        captureEngine.setTranslationBusy(true)

        activeTranslateTask = Task { @MainActor [weak self] in
            guard let self else { return }
            defer {
                if self.batchGeneration == myGen {
                    self.captureEngine.setTranslationBusy(false)
                }
            }

            for (stepOffset, cardIndex) in orderedUnresolved.enumerated() {
                if Task.isCancelled || self.batchGeneration != myGen {
                    return
                }
                let cluster = selected[cardIndex]
                let progressPrefix = (orderedUnresolved.count > 1)
                    ? "(\(stepOffset + 1)/\(orderedUnresolved.count)) "
                    : ""

                // 直前のステップで同一文が訳されていれば再利用
                if let hit = ClusterTranslationPlanner.findCachedTranslation(for: cluster, in: self.cachedClusterEntries) {
                    self.updateSingleCard(at: cardIndex, generation: myGen, finalTranslation: hit)
                    continue
                }

                let outcome = await self.translator.translate(
                    text: cluster.text,
                    source: srcLang,
                    target: tgtLang,
                    backend: backend,
                    runtimeDir: runtimeDir,
                    onStageUpdate: { [weak self] stageText in
                        guard let self, self.batchGeneration == myGen else { return }
                        self.mainWindow.viewModel.updateStage("\(progressPrefix)\(stageText)", busy: true)
                    },
                    onPartialStream: { [weak self] partial in
                        guard let self, self.batchGeneration == myGen else { return }
                        self.updateSingleCardStreaming(at: cardIndex, generation: myGen, partial: partial)
                    }
                )

                if Task.isCancelled || self.batchGeneration != myGen {
                    return
                }

                switch outcome {
                case .translated(let targetText, _):
                    self.recordCachedClusterTranslation(sourceText: cluster.text, translatedText: targetText)
                    self.updateSingleCard(at: cardIndex, generation: myGen, finalTranslation: targetText)
                case .skipped(let reason):
                    self.markSingleCardFinished(at: cardIndex, generation: myGen)
                    if orderedUnresolved.count == 1 {
                        self.mainWindow.viewModel.presentSkipped(reason)
                    }
                case .languagePackRequired(let sCode, let tCode):
                    self.markSingleCardFinished(at: cardIndex, generation: myGen)
                    self.mainWindow.viewModel.updateStage("言語データ未導入", busy: false)
                    if !self.mainWindow.viewModel.hasVisibleContent {
                        self.mainWindow.viewModel.statusNotice = "macOSの翻訳言語データ（\(sCode) → \(tCode)）をダウンロードしてください（またはエンジンを PLaMo へ設定）"
                    }
                    return
                case .failed(let reason):
                    self.markSingleCardFinished(at: cardIndex, generation: myGen)
                    if reason != "キャンセル" {
                        self.mainWindow.viewModel.updateStage("エラー", busy: false)
                        if !self.mainWindow.viewModel.hasVisibleContent {
                            self.mainWindow.viewModel.statusNotice = reason
                        }
                    }
                }
            }

            guard self.batchGeneration == myGen else { return }
            self.activeTranslateTask = nil
            self.activeUnresolvedKey = ""
            let vm = self.mainWindow.viewModel
            let joined = vm.clusterCards.map(\.displayBodyText).filter { !$0.isEmpty }.joined(separator: "\n\n")
            if !joined.isEmpty {
                vm.translatedText = joined
            }
            vm.updateStage(vm.isCapturing ? "待機中" : "", busy: false)
            self.syncInPlaceOverlay()
        }
    }

    private func updateSingleCardStreaming(at index: Int, generation: UInt64, partial: String) {
        guard batchGeneration == generation else { return }
        let vm = mainWindow.viewModel
        guard vm.clusterCards.indices.contains(index) else { return }
        vm.clusterCards[index].streamingText = partial
        vm.clusterCards[index].isTranslating = true
        syncInPlaceOverlay()
    }

    private func updateSingleCard(at index: Int, generation: UInt64, finalTranslation: String) {
        guard batchGeneration == generation else { return }
        let vm = mainWindow.viewModel
        guard vm.clusterCards.indices.contains(index) else { return }
        vm.clusterCards[index].streamingText = ""
        vm.clusterCards[index].translatedText = finalTranslation
        vm.clusterCards[index].isTranslating = false
        syncInPlaceOverlay()
    }

    private func markSingleCardFinished(at index: Int, generation: UInt64) {
        guard batchGeneration == generation else { return }
        let vm = mainWindow.viewModel
        guard vm.clusterCards.indices.contains(index) else { return }
        vm.clusterCards[index].streamingText = ""
        vm.clusterCards[index].isTranslating = false
        syncInPlaceOverlay()
    }

    // MARK: - Actions

    @objc private func selectRegionAction() {
        inPlaceOverlay.hide()
        regionSelector.beginSelection { [weak self] selection in
            Task { @MainActor in
                guard let self, let selection else { return }
                self.currentSelection = selection
                self.latestRawClusters.removeAll()
                self.mainWindow.makeKeyAndOrderFront(nil)
                self.refreshRegionIndicator()
                await self.startCapture(with: selection)
            }
        }
    }

    @objc private func toggleCaptureAction() {
        Task { @MainActor in
            if captureEngine.isRunning {
                cancelActiveTranslationBatch()
                await captureEngine.stop()
                hoveredClusterCard = nil
                regionIndicator.orderOut(nil)
                inPlaceOverlay.hide()
                syncRunningUI(isRunning: false)
                mainWindow.viewModel.updateStage("", busy: false)
                if !mainWindow.viewModel.hasVisibleContent {
                    mainWindow.viewModel.statusNotice = "一時停止中（再生ボタンで再開）"
                }
            } else if let selection = currentSelection {
                await startCapture(with: selection)
            } else {
                selectRegionAction()
            }
        }
    }

    @objc private func toggleCollapseAction() {
        mainWindow.toggleCollapse(animated: true)
    }

    @objc private func toggleFloatingAction() {
        let nextFloating = (mainWindow.level != .floating)
        mainWindow.level = nextFloating ? .floating : .normal
        floatMenuItem?.state = nextFloating ? .on : .off
    }

    @objc private func toggleCornersAction() {
        showsRegionCorners.toggle()
        cornersMenuItem?.state = showsRegionCorners ? .on : .off
        refreshRegionIndicator()
    }

    private func startCapture(with selection: CaptureSelection) async {
        do {
            ensureSelectedBackendRunning()
            mainWindow.viewModel.updateStage("待機中", busy: false)
            mainWindow.viewModel.statusNotice = "監視中（\(mainWindow.viewModel.displayMode.shortName)モード / \(mainWindow.viewModel.backend.shortName)）"
            try await captureEngine.start(
                selection: selection,
                sourceLangCode: mainWindow.viewModel.sourceLanguage.code,
                targetLangCode: mainWindow.viewModel.targetLanguage.code
            )
            syncRunningUI(isRunning: true)
            refreshRegionIndicator()
            syncInPlaceOverlay()
        } catch {
            syncRunningUI(isRunning: false)
            mainWindow.viewModel.statusNotice = "画面収録を許可してください（システム設定 → プライバシーとセキュリティ → 画面収録）"
        }
    }

    private func syncRunningUI(isRunning: Bool) {
        mainWindow.viewModel.isCapturing = isRunning
        updateSpotCursorPolling()
        if !isRunning {
            mainWindow.viewModel.isPipelineBusy = false
            mainWindow.viewModel.streamingTranslatedText = ""
            inPlaceOverlay.hide()
        }
    }

    @objc private func updateAndRelaunchAction() {
        let vm = mainWindow.viewModel
        guard !vm.isUpdatingApp else { return }

        guard let root = projectRootURL else {
            vm.updateStage("更新元不明", busy: false)
            vm.statusNotice = "プロジェクトディレクトリ（Package.swift）が見つかりません"
            return
        }

        let bundleScript = root.appendingPathComponent("scripts/bundle_app.sh")
        let appBundlePath = root.appendingPathComponent("build/ScreenTranslate.app").path
        guard FileManager.default.fileExists(atPath: bundleScript.path) else {
            vm.updateStage("スクリプトなし", busy: false)
            vm.statusNotice = "scripts/bundle_app.sh が見つかりません"
            return
        }

        vm.isUpdatingApp = true
        vm.updateStage("更新ビルド中…", busy: true)

        Task { @MainActor [weak self] in
            guard let self else { return }
            self.cancelActiveTranslationBatch()
            await self.captureEngine.stop()
            self.regionIndicator.orderOut(nil)
            self.inPlaceOverlay.hide()
            self.syncRunningUI(isRunning: false)
            self.mainWindow.viewModel.updateStage("更新ビルド中…", busy: true)

            let result: (exitCode: Int32, stderrTail: String) = await withCheckedContinuation { cont in
                DispatchQueue.global(qos: .userInitiated).async {
                    let proc = Process()
                    proc.executableURL = URL(fileURLWithPath: "/bin/bash")
                    proc.arguments = [bundleScript.path]
                    proc.currentDirectoryURL = root
                    let errPipe = Pipe()
                    proc.standardOutput = FileHandle.nullDevice
                    proc.standardError = errPipe

                    do {
                        try proc.run()
                        let errData = errPipe.fileHandleForReading.readDataToEndOfFile()
                        proc.waitUntilExit()
                        let errStr = String(data: errData, encoding: .utf8)?
                            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                        let tail = String(errStr.suffix(220))
                        cont.resume(returning: (proc.terminationStatus, tail))
                    } catch {
                        cont.resume(returning: (1, error.localizedDescription))
                    }
                }
            }

            if result.exitCode == 0 {
                vm.updateStage("再起動中…", busy: true)
                let relaunchProc = Process()
                relaunchProc.executableURL = URL(fileURLWithPath: "/bin/bash")
                let escapedApp = appBundlePath.replacingOccurrences(of: "'", with: "'\\''")
                relaunchProc.arguments = ["-c", "sleep 0.35 && /usr/bin/open -n '\(escapedApp)' >/dev/null 2>&1 &"]
                relaunchProc.currentDirectoryURL = root
                relaunchProc.standardOutput = FileHandle.nullDevice
                relaunchProc.standardError = FileHandle.nullDevice
                try? relaunchProc.run()
                self.performCleanExit(code: 0, stopSidecar: false)
            } else {
                vm.isUpdatingApp = false
                vm.updateStage("更新失敗", busy: false)
                vm.statusNotice = result.stderrTail.isEmpty
                    ? "更新ビルドに失敗しました（exit \(result.exitCode)）"
                    : "更新ビルドに失敗しました:\n\(result.stderrTail)"
            }
        }
    }

    @objc private func quitAction() {
        performCleanExit(code: 0, stopSidecar: true)
    }
}
