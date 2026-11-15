import Foundation
import AppKit
import SwiftUI
import Translation
#if canImport(ScreenTranslateCore)
import ScreenTranslateCore
#endif

/// PLaMo 3 Translate ローカル推論サーバー（llama-server / mlx-lm）および macOS 標準翻訳の統合コーディネータ。
/// - PLaMo 3 選択時: PFML3プロンプト＋直近2発話の文脈履歴を付与し、SSEで翻訳トークンをリアルタイム逐次配信する。
/// - サーバー起動中・初回モデル取得中は .runtime/plamo_status.json を監視して進捗メッセージをUIへ中継し、準備完了次第そのまま翻訳を実行する。
/// - OS標準翻訳選択時: 言語ペアごとの TranslationSession を再利用してコールドスタート負荷を防ぐ。
@MainActor
final class SystemTranslationCoordinator: ObservableObject {
    @Published var configuration: TranslationSession.Configuration?

    static let defaultPLaMoPort: Int = 18492

    private var cache = BoundedLRUCache<String, String>(capacity: 128)
    private var historyTurns: [PFML3PromptBuilder.HistoryTurn] = []
    private var cachedRemoteModelID: String?
    private var cachedDirectSessions: [String: AnyObject] = [:]

    private struct SessionJob {
        let id: UUID
        let text: String
        let sourceCode: String
        let targetCode: String
        let continuation: CheckedContinuation<TranslationOutcome, Never>
    }

    private var pendingJobs: [SessionJob] = []
    private var activePairKey: String = ""
    private var sessionWakeContinuation: AsyncStream<Void>.Continuation?

    func clearCache() {
        cache.clear()
        historyTurns.removeAll()
        cachedRemoteModelID = nil
    }

    func resetModelCache() {
        cachedRemoteModelID = nil
    }

    func preparePairIfConcrete(source: TranslationLanguage, target: TranslationLanguage, backend: TranslationBackendKind) {
        guard !backend.isPLaMo else { return }
        let srcCode = (source.code == "auto") ? ((target.code == "ja") ? "en" : "ja") : source.code
        guard srcCode != target.code else { return }
        ensureViewConfiguration(sourceCode: srcCode, targetCode: target.code)
    }

    func translate(
        text: String,
        source: TranslationLanguage,
        target: TranslationLanguage,
        backend: TranslationBackendKind,
        runtimeDir: URL?,
        onStageUpdate: @escaping @MainActor (String) -> Void,
        onPartialStream: @escaping @MainActor (String) -> Void
    ) async -> TranslationOutcome {
        let decision = TranslatabilityGate.evaluate(
            rawText: text,
            selectedSourceCode: source.code,
            targetCode: target.code
        )

        let cleanedText: String
        let resolvedSource: String
        switch decision {
        case .skip(let reason):
            return .skipped(reason)
        case .translate(let cleaned, let srcCode, _):
            cleanedText = cleaned
            resolvedSource = srcCode
        }

        let cacheKey = "\(backend.rawValue)|\(resolvedSource)->\(target.code)|\(cleanedText)"
        if let hit = cache.get(cacheKey) {
            return .translated(hit, fromCache: true)
        }

        if backend.isPLaMo {
            return await translateWithPLaMo(
                cleanedText: cleanedText,
                resolvedSource: resolvedSource,
                targetCode: target.code,
                backend: backend,
                cacheKey: cacheKey,
                runtimeDir: runtimeDir,
                onStageUpdate: onStageUpdate,
                onPartialStream: onPartialStream
            )
        } else {
            onStageUpdate("OS標準翻訳中…（\(cleanedText.count)字）")
            return await translateWithAppleSystem(
                cleanedText: cleanedText,
                resolvedSource: resolvedSource,
                targetCode: target.code,
                cacheKey: cacheKey
            )
        }
    }

    // MARK: - PLaMo 3 Translate Streaming Engine

    private static func makeLoopbackURL(port: Int, path: String) -> URL? {
        if let customBase = ProcessInfo.processInfo.environment["SCREEN_TRANSLATE_PLAMO_BASE"],
           !customBase.isEmpty,
           let base = URL(string: customBase) {
            return base.appendingPathComponent(path.hasPrefix("/") ? String(path.dropFirst()) : path)
        }
        var comp = URLComponents()
        comp.scheme = "http"
        comp.host = "127.0.0.1"
        comp.port = port
        comp.path = path
        return comp.url
    }

    private func readPLaMoSidecarStatus(runtimeDir: URL?) -> (state: String, message: String)? {
        guard let dir = runtimeDir else { return nil }
        let statusURL = dir.appendingPathComponent("plamo_status.json")
        guard let data = try? Data(contentsOf: statusURL),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        let st = (json["state"] as? String) ?? ""
        let msg = (json["message"] as? String) ?? ""
        return (st, msg)
    }

    private func discoverModelID(port: Int) async -> String? {
        if let cached = cachedRemoteModelID {
            return cached
        }
        guard let url = Self.makeLoopbackURL(port: port, path: "/v1/models") else { return nil }
        var req = URLRequest(url: url)
        req.timeoutInterval = 1.5
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              let http = resp as? HTTPURLResponse,
              (200..<300).contains(http.statusCode),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = json["data"] as? [[String: Any]],
              let firstID = list.first?["id"] as? String,
              !firstID.isEmpty else {
            return nil
        }
        cachedRemoteModelID = firstID
        return firstID
    }

    private func waitUntilPLaMoReady(
        backend: TranslationBackendKind,
        runtimeDir: URL?,
        onStageUpdate: @escaping @MainActor (String) -> Void
    ) async -> Result<String, Error> {
        let port = Self.defaultPLaMoPort
        if let modelID = await discoverModelID(port: port) {
            return .success(modelID)
        }

        // 初回モデルダウンロードや起動中の場合、最大180秒まで0.6秒間隔で進捗を表示しながら待機
        for tick in 0..<300 {
            if Task.isCancelled {
                return .failure(CancellationError())
            }
            if let sidecar = readPLaMoSidecarStatus(runtimeDir: runtimeDir) {
                if sidecar.state == "error" {
                    let errMsg = sidecar.message.isEmpty ? "\(backend.shortName) の起動に失敗しました" : sidecar.message
                    return .failure(NSError(domain: "PLaMo", code: 2, userInfo: [NSLocalizedDescriptionKey: errMsg]))
                }
                if !sidecar.message.isEmpty {
                    onStageUpdate(sidecar.message)
                } else {
                    onStageUpdate("\(backend.shortName) を準備中…")
                }
            } else if tick == 0 {
                onStageUpdate("\(backend.shortName) ローカルサーバーへ接続中…")
            }

            try? await Task.sleep(nanoseconds: 600_000_000)
            if let modelID = await discoverModelID(port: port) {
                return .success(modelID)
            }
        }

        return .failure(
            NSError(
                domain: "PLaMo",
                code: 3,
                userInfo: [NSLocalizedDescriptionKey: "\(backend.shortName) サーバーが応答しません（メニュー『翻訳エンジン』から再起動またはOS標準へ切替可能）"]
            )
        )
    }

    private func translateWithPLaMo(
        cleanedText: String,
        resolvedSource: String,
        targetCode: String,
        backend: TranslationBackendKind,
        cacheKey: String,
        runtimeDir: URL?,
        onStageUpdate: @escaping @MainActor (String) -> Void,
        onPartialStream: @escaping @MainActor (String) -> Void
    ) async -> TranslationOutcome {
        let readyResult = await waitUntilPLaMoReady(
            backend: backend,
            runtimeDir: runtimeDir,
            onStageUpdate: onStageUpdate
        )

        let modelID: String
        switch readyResult {
        case .success(let id):
            modelID = id
        case .failure(let err):
            if err is CancellationError || Task.isCancelled {
                return .failed("キャンセル")
            }
            return .failed(err.localizedDescription)
        }

        guard let completionsURL = Self.makeLoopbackURL(port: Self.defaultPLaMoPort, path: "/v1/completions") else {
            return .failed("ローカル推論URLの構築に失敗しました")
        }

        // 短い見出しや単語単体（25文字未満）に直前キャラ名等の文脈履歴が付くと誤補完の原因になるため、十分な文脈がある文のみ履歴を付与する
        let activeHistory = (cleanedText.count >= 25) ? historyTurns : []
        let prompt = PFML3PromptBuilder.buildPrompt(
            sourceText: cleanedText,
            sourceLangCode: resolvedSource,
            targetLangCode: targetCode,
            history: activeHistory
        )
        let maxTokens = PFML3PromptBuilder.recommendedMaxTokens(for: cleanedText)

        let payload: [String: Any] = [
            "model": modelID,
            "prompt": prompt,
            "temperature": 0.0,
            "max_tokens": maxTokens,
            "stream": true,
            "stop": ["<|plamo:tag|>", "<|plamo:bos|>", "<|plamo:eos|>"]
        ]

        guard let bodyData = try? JSONSerialization.data(withJSONObject: payload) else {
            return .failed("リクエスト生成エラー")
        }

        var req = URLRequest(url: completionsURL)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.timeoutInterval = 60.0
        req.httpBody = bodyData

        onStageUpdate("\(backend.shortName) 翻訳中…（原文 \(cleanedText.count)字）")

        do {
            let (bytes, response) = try await URLSession.shared.bytes(for: req)
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                cachedRemoteModelID = nil
                return .failed("\(backend.shortName) 推論応答エラー（再試行します）")
            }

            var accumulated = ""
            var rawFallbackLines: [String] = []
            var lastUIEmitTime = CFAbsoluteTimeGetCurrent()

            for try await line in bytes.lines {
                if Task.isCancelled {
                    return .failed("キャンセル")
                }
                if line.hasPrefix("data:") {
                    let (chunk, isDone) = PFML3PromptBuilder.parseSSELine(line)
                    if let chunk, !chunk.isEmpty {
                        accumulated += chunk
                        let now = CFAbsoluteTimeGetCurrent()
                        // UIの再描画頻度を最大約8fps(120ms間隔)に抑えてメインスレッド＆ブラー合成負荷を極小化
                        if (now - lastUIEmitTime) >= 0.12 {
                            let cleanPartial = PFML3PromptBuilder.stripStopTokens(accumulated)
                            if !cleanPartial.isEmpty {
                                onPartialStream(cleanPartial)
                                onStageUpdate("\(backend.shortName) 生成中… \(cleanPartial.count)字")
                            }
                            lastUIEmitTime = now
                        }
                    }
                    if isDone {
                        break
                    }
                } else if !line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    rawFallbackLines.append(line)
                }
            }

            // 非SSE応答のフォールバック解釈
            if accumulated.isEmpty && !rawFallbackLines.isEmpty {
                let combined = rawFallbackLines.joined(separator: "\n")
                if let data = combined.data(using: .utf8),
                   let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let choices = json["choices"] as? [[String: Any]],
                   let text = choices.first?["text"] as? String {
                    accumulated = text
                }
            }

            let finalOut = PFML3PromptBuilder.stripStopTokens(accumulated)
            guard !finalOut.isEmpty else {
                return .failed("\(backend.shortName) の翻訳結果が空でした")
            }

            cache.set(cacheKey, value: finalOut)
            appendHistory(
                sourceLang: resolvedSource,
                targetLang: targetCode,
                sourceText: cleanedText,
                translatedText: finalOut
            )
            return .translated(finalOut, fromCache: false)
        } catch {
            if Task.isCancelled {
                return .failed("キャンセル")
            }
            cachedRemoteModelID = nil
            return .failed("\(backend.shortName) 通信エラー: \(error.localizedDescription)")
        }
    }

    private func appendHistory(sourceLang: String, targetLang: String, sourceText: String, translatedText: String) {
        // 長すぎる全文記事を履歴に積むとプロンプト処理が重くなるため、450文字以下の発話・段落のみ直近2件保持
        guard sourceText.count <= 450 && translatedText.count <= 600 else { return }
        historyTurns.append(
            PFML3PromptBuilder.HistoryTurn(
                sourceLang: sourceLang,
                targetLang: targetLang,
                sourceText: sourceText,
                translatedText: translatedText
            )
        )
        if historyTurns.count > 4 {
            historyTurns.removeFirst(historyTurns.count - 4)
        }
    }

    // MARK: - macOS Built-in Translation Fallback (Cached Session)

    private func translateWithAppleSystem(
        cleanedText: String,
        resolvedSource: String,
        targetCode: String,
        cacheKey: String
    ) async -> TranslationOutcome {
        let srcLocale = Locale.Language(identifier: resolvedSource)
        let tgtLocale = Locale.Language(identifier: targetCode)

        let availability = LanguageAvailability()
        let status = await availability.status(from: srcLocale, to: tgtLocale)

        if status == .unsupported {
            return .failed("この言語ペア（\(resolvedSource) → \(targetCode)）はOS標準翻訳で未対応です")
        }

        // macOS 26以降: 言語ペアごとに1つの TranslationSession インスタンスを再利用し、毎回の再初期化コストを排除
        if status == .installed, #available(macOS 26.0, *) {
            let pairKey = "\(resolvedSource)->\(targetCode)"
            let directSession: TranslationSession
            if let existing = cachedDirectSessions[pairKey] as? TranslationSession {
                directSession = existing
            } else {
                let created = TranslationSession(installedSource: srcLocale, target: tgtLocale)
                cachedDirectSessions[pairKey] = created
                directSession = created
            }
            do {
                let response = try await directSession.translate(cleanedText)
                let out = response.targetText.trimmingCharacters(in: .whitespacesAndNewlines)
                if !out.isEmpty {
                    cache.set(cacheKey, value: out)
                    return .translated(out, fromCache: false)
                }
            } catch {
                cachedDirectSessions.removeValue(forKey: pairKey)
            }
        }

        return await withCheckedContinuation { cont in
            let job = SessionJob(
                id: UUID(),
                text: cleanedText,
                sourceCode: resolvedSource,
                targetCode: targetCode,
                continuation: cont
            )
            pendingJobs.append(job)
            ensureViewConfiguration(sourceCode: resolvedSource, targetCode: targetCode)
            sessionWakeContinuation?.yield(())

            let jobID = job.id
            Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 12_000_000_000)
                guard let self else { return }
                if let idx = self.pendingJobs.firstIndex(where: { $0.id == jobID }) {
                    let expired = self.pendingJobs.remove(at: idx)
                    expired.continuation.resume(
                        returning: .languagePackRequired(sourceCode: resolvedSource, targetCode: targetCode)
                    )
                }
            }
        }
    }

    private func ensureViewConfiguration(sourceCode: String, targetCode: String) {
        let key = "\(sourceCode)->\(targetCode)"
        let src = Locale.Language(identifier: sourceCode)
        let tgt = Locale.Language(identifier: targetCode)

        if configuration == nil || activePairKey != key {
            activePairKey = key
            configuration = TranslationSession.Configuration(source: src, target: tgt)
        } else {
            configuration?.invalidate()
        }
    }

    func handleViewTranslationSession(_ session: TranslationSession) async {
        do {
            try await session.prepareTranslation()
        } catch {
            drainAllWithError(.languagePackRequired(sourceCode: activePairKey, targetCode: ""))
            return
        }

        await processMatchingJobs(using: session)

        let stream = AsyncStream<Void> { cont in
            self.sessionWakeContinuation = cont
        }
        for await _ in stream {
            if Task.isCancelled { break }
            await processMatchingJobs(using: session)
        }
    }

    private func processMatchingJobs(using session: TranslationSession) async {
        while let first = pendingJobs.first {
            let jobKey = "\(first.sourceCode)->\(first.targetCode)"
            if jobKey != activePairKey {
                ensureViewConfiguration(sourceCode: first.sourceCode, targetCode: first.targetCode)
                return
            }
            let job = pendingJobs.removeFirst()
            let normalized = TextStabilityGate.normalize(job.text)
            let cacheKey = "apple-system|\(job.sourceCode)->\(job.targetCode)|\(normalized)"

            if let hit = cache.get(cacheKey) {
                job.continuation.resume(returning: .translated(hit, fromCache: true))
                continue
            }

            do {
                let response = try await session.translate(job.text)
                let out = response.targetText.trimmingCharacters(in: .whitespacesAndNewlines)
                if !out.isEmpty {
                    cache.set(cacheKey, value: out)
                    job.continuation.resume(returning: .translated(out, fromCache: false))
                } else {
                    job.continuation.resume(returning: .failed("翻訳結果が空でした"))
                }
            } catch {
                job.continuation.resume(
                    returning: .languagePackRequired(sourceCode: job.sourceCode, targetCode: job.targetCode)
                )
            }
        }
    }

    private func drainAllWithError(_ outcome: TranslationOutcome) {
        let jobs = pendingJobs
        pendingJobs.removeAll()
        for job in jobs {
            job.continuation.resume(returning: outcome)
        }
    }
}
