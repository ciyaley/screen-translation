import AppKit

let args = CommandLine.arguments
if args.contains("--self-test-ui") || args.contains("--self-test-daemon") {
    // CLI/サンドボックス内テスト時は HIServices (_RegisterApplication) を経由せず RunLoop.main 上で実UI階層と終了チャネルを検証する
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let controller = AppController()
        controller.startForTesting(mode: args.contains("--self-test-ui") ? .selfTestUI : .selfTestDaemon)
        RunLoop.main.run()
    }
} else {
    let app = NSApplication.shared
    let delegate = MainActor.assumeIsolated { AppController() }
    app.delegate = delegate
    app.run()
}
