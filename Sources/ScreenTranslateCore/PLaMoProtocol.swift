import Foundation

/// PLaMo 3 Translate エンジン種別および macOS 標準翻訳の選択モデル
public enum TranslationBackendKind: String, CaseIterable, Equatable, Sendable, Identifiable {
    case plamo2B = "plamo-2b"
    case plamo08B = "plamo-0.8b"
    case plamo8B = "plamo-8b"
    case systemApple = "apple-system"

    public var id: String { rawValue }

    public var menuTitle: String {
        switch self {
        case .plamo2B:
            return "PLaMo 3 Translate 2B（推奨・1.8GB）"
        case .plamo08B:
            return "PLaMo 3 Translate 0.8B（超軽量・0.6GB）"
        case .plamo8B:
            return "PLaMo 3 Translate 8B（高精度・5.3GB）"
        case .systemApple:
            return "macOS 標準翻訳（追加メモリなし）"
        }
    }

    public var shortName: String {
        switch self {
        case .plamo2B: return "PLaMo 2B"
        case .plamo08B: return "PLaMo 0.8B"
        case .plamo8B: return "PLaMo 8B"
        case .systemApple: return "OS標準"
        }
    }

    public var plamoSizeArg: String? {
        switch self {
        case .plamo2B: return "2b"
        case .plamo08B: return "0.8b"
        case .plamo8B: return "8b"
        case .systemApple: return nil
        }
    }

    public var isPLaMo: Bool {
        self != .systemApple
    }
}

/// PLaMo 3 Translate 公式 PFML3 プロンプトビルダおよびローカル推論サーバー通信ヘルパ
/// （llama-server / mlx-lm serve.py 両対応、直近文脈ペア保持・SSEストリーミング対応）
public enum PFML3PromptBuilder {
    public struct HistoryTurn: Equatable, Sendable {
        public let sourceLang: String
        public let targetLang: String
        public let sourceText: String
        public let translatedText: String

        public init(sourceLang: String, targetLang: String, sourceText: String, translatedText: String) {
            self.sourceLang = sourceLang
            self.targetLang = targetLang
            self.sourceText = sourceText
            self.translatedText = translatedText
        }
    }

    /// アプリ内の言語コードを PLaMo 3 Translate 公式の PFML3 言語コードへ正規化する
    public static func plamoLangCode(for appCode: String) -> String {
        switch appCode {
        case "zh-Hans": return "zh-hans"
        case "zh-Hant": return "zh-hant"
        default: return appCode.lowercased()
        }
    }

    /// PLaMo 3 Translate 公式仕様に完全準拠した PFML3 プロンプト文字列を生成する。
    /// （先頭の <|plamo:bos|> は llama-server / mlx-lm 側が自動付与するため省略する公式推奨形式）
    public static func buildPrompt(
        sourceText: String,
        sourceLangCode: String,
        targetLangCode: String,
        history: [HistoryTurn] = [],
        japaneseForm: String? = nil
    ) -> String {
        let srcCode = plamoLangCode(for: sourceLangCode)
        let tgtCode = plamoLangCode(for: targetLangCode)

        var out = "<|plamo:tag|>dataset<|plamo:msg|>translation"

        // 同一言語方向の直近文脈ペア（最大2件）を付与して固有名詞・口調の一貫性を保つ
        let matchingHistory = history.filter {
            plamoLangCode(for: $0.sourceLang) == srcCode && plamoLangCode(for: $0.targetLang) == tgtCode
        }.suffix(2)

        for turn in matchingHistory {
            let cleanSrc = sanitizeControlTokens(turn.sourceText)
            let cleanTgt = sanitizeControlTokens(turn.translatedText)
            if !cleanSrc.isEmpty && !cleanTgt.isEmpty {
                out += "<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>\(srcCode)<|plamo:msg|>\(cleanSrc)"
                out += "<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>\(tgtCode)<|plamo:msg|>\(cleanTgt)"
            }
        }

        let cleanCurrent = sanitizeControlTokens(sourceText)
        out += "<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>\(srcCode)<|plamo:msg|>\(cleanCurrent)"
        out += "<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>\(tgtCode)"

        if tgtCode == "ja", let form = japaneseForm, (form == "plain" || form == "polite") {
            out += "<|plamo:key|>form<|plamo:val|>\(form)"
        }

        out += "<|plamo:msg|>"
        return out
    }

    /// 出力や入力に混入した PFML 特殊トークンを安全に除去する
    public static func stripStopTokens(_ raw: String) -> String {
        var s = raw
        let stopMarkers = ["<|plamo:tag|>", "<|plamo:bos|>", "<|plamo:eos|>", "<|endoftext|>"]
        for marker in stopMarkers {
            if let range = s.range(of: marker) {
                s = String(s[..<range.lowerBound])
            }
        }
        return s.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func sanitizeControlTokens(_ text: String) -> String {
        text
            .replacingOccurrences(of: "<|plamo:", with: "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// 原文文字数に応じた安全な生成上限トークン数（長文切れ防止＋暴走防止）
    public static func recommendedMaxTokens(for sourceText: String) -> Int {
        let count = sourceText.count
        return min(2048, max(160, count * 2 + 96))
    }

    /// OpenAI互換 SSE ("data: {...}") の1行からテキスト差分を抽出する（/v1/completions と /v1/chat/completions 両対応）
    public static func parseSSELine(_ line: String) -> (chunk: String?, isDone: Bool) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("data:") else {
            return (nil, false)
        }
        let payload = trimmed.dropFirst(5).trimmingCharacters(in: .whitespaces)
        if payload == "[DONE]" {
            return (nil, true)
        }
        guard let data = payload.data(using: .utf8),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let choices = json["choices"] as? [[String: Any]],
              let first = choices.first else {
            return (nil, false)
        }

        if let text = first["text"] as? String {
            let finish = first["finish_reason"] as? String
            return (text, finish != nil && !(finish?.isEmpty ?? true))
        }
        if let delta = first["delta"] as? [String: Any], let content = delta["content"] as? String {
            let finish = first["finish_reason"] as? String
            return (content, finish != nil && !(finish?.isEmpty ?? true))
        }
        if let finish = first["finish_reason"] as? String, !finish.isEmpty {
            return (nil, true)
        }
        return (nil, false)
    }
}
