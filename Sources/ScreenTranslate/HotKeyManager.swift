import Foundation
import Carbon

/// アクセシビリティ権限不要の Carbon RegisterEventHotKey によるグローバルショートカット。
final class HotKeyManager {
    enum Action: UInt32 {
        case selectRegion = 1       // ⌥⇧R : 範囲選択
        case toggleCapture = 2      // ⌥⇧T : 監視開始/停止
        case toggleClickThrough = 3 // ⌥⇧L : 折りたたみ切替
        case toggleVisibility = 4   // ⌥⇧W : パネル表示/非表示
        case quitApp = 5            // ⌥⇧Q : アプリ即終了
        case cycleDisplayMode = 6   // ⌥⇧M : 表示モード切替（カード / 直接重畳 / 近傍のみ）
    }

    var onAction: ((Action) -> Void)?
    private var hotKeyRefs: [EventHotKeyRef] = []
    private var eventHandlerRef: EventHandlerRef?

    private static weak var sharedInstance: HotKeyManager?

    func registerDefaults() {
        unregisterAll()
        Self.sharedInstance = self

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )

        let status = InstallEventHandler(
            GetApplicationEventTarget(),
            { _, event, _ -> OSStatus in
                guard let event else { return OSStatus(eventNotHandledErr) }
                var hotKeyID = EventHotKeyID()
                let err = GetEventParameter(
                    event,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &hotKeyID
                )
                if err == noErr, let action = Action(rawValue: hotKeyID.id) {
                    DispatchQueue.main.async {
                        HotKeyManager.sharedInstance?.onAction?(action)
                    }
                }
                return noErr
            },
            1,
            &eventType,
            nil,
            &eventHandlerRef
        )

        guard status == noErr else { return }

        let modifiers = UInt32(optionKey | shiftKey)
        register(keyCode: UInt32(kVK_ANSI_R), modifiers: modifiers, action: .selectRegion)
        register(keyCode: UInt32(kVK_ANSI_T), modifiers: modifiers, action: .toggleCapture)
        register(keyCode: UInt32(kVK_ANSI_L), modifiers: modifiers, action: .toggleClickThrough)
        register(keyCode: UInt32(kVK_ANSI_W), modifiers: modifiers, action: .toggleVisibility)
        register(keyCode: UInt32(kVK_ANSI_Q), modifiers: modifiers, action: .quitApp)
        register(keyCode: UInt32(kVK_ANSI_M), modifiers: modifiers, action: .cycleDisplayMode)
    }

    private func register(keyCode: UInt32, modifiers: UInt32, action: Action) {
        let signature: OSType = 0x5354524E // 'STRN'
        let hotKeyID = EventHotKeyID(signature: signature, id: action.rawValue)
        var ref: EventHotKeyRef?
        let err = RegisterEventHotKey(
            keyCode,
            modifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &ref
        )
        if err == noErr, let ref {
            hotKeyRefs.append(ref)
        }
    }

    func unregisterAll() {
        for ref in hotKeyRefs {
            UnregisterEventHotKey(ref)
        }
        hotKeyRefs.removeAll()
        if let handler = eventHandlerRef {
            RemoveEventHandler(handler)
            eventHandlerRef = nil
        }
    }

    deinit {
        unregisterAll()
    }
}
