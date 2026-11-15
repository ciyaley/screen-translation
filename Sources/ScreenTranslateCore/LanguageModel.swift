import Foundation
import NaturalLanguage

public struct TranslationLanguage: Equatable, Hashable, Identifiable, Sendable {
    public let code: String      // "auto", "en", "ja", "zh-Hans", "zh-Hant", "ko", "fr", "de", "es", "ru"
    public let shortName: String // ヘッダー表示用の短い日本語名
    public var id: String { code }

    public init(code: String, shortName: String) {
        self.code = code
        self.shortName = shortName
    }

    public static let auto = TranslationLanguage(code: "auto", shortName: "自動")

    public static let concreteLanguages: [TranslationLanguage] = [
        TranslationLanguage(code: "en", shortName: "英語"),
        TranslationLanguage(code: "ja", shortName: "日本語"),
        TranslationLanguage(code: "zh-Hans", shortName: "中国語(簡)"),
        TranslationLanguage(code: "zh-Hant", shortName: "中国語(繁)"),
        TranslationLanguage(code: "ko", shortName: "韓国語"),
        TranslationLanguage(code: "fr", shortName: "仏語"),
        TranslationLanguage(code: "de", shortName: "独語"),
        TranslationLanguage(code: "es", shortName: "西語"),
        TranslationLanguage(code: "ru", shortName: "露語")
    ]

    public static let sourceOptions: [TranslationLanguage] = [auto] + concreteLanguages
    public static let targetOptions: [TranslationLanguage] = concreteLanguages

    /// "auto" 選択時にスクリプト構成＋NaturalLanguage確率分布を用いて言語コードを推定する
    public static func resolveSourceCode(selectedCode: String, sampleText: String, targetCode: String) -> String {
        if selectedCode != "auto" {
            return selectedCode
        }
        switch TranslatabilityGate.evaluate(
            rawText: sampleText,
            selectedSourceCode: "auto",
            targetCode: targetCode
        ) {
        case .translate(_, let resolvedCode, _):
            return resolvedCode
        case .skip(let reason):
            if reason == .alreadyTargetLanguage {
                return targetCode
            }
            return (targetCode == "ja") ? "en" : "ja"
        }
    }
}

public enum TranslationOutcome: Equatable, Sendable {
    case translated(String, fromCache: Bool)
    case skipped(SkipReason)
    case languagePackRequired(sourceCode: String, targetCode: String)
    case failed(String)
}
