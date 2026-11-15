import Foundation
import CoreVideo

/// macOS 26 TextRecognition.framework の .fast ROIデコード時 SIGTRAP (空配列 objectAtSubscript(0)) を確実に回避し、
/// Apple Neural Engine 推論 (.accurate) と 32BGRA バッファで安定動作させるための不変設定ポリシー。
public struct ResolvedVisionOCRSpec: Equatable, Sendable {
    public let pixelFormat: OSType
    public let usesFastLevel: Bool
    public let usesLanguageCorrection: Bool
    public let automaticallyDetectsLanguage: Bool
    public let recognitionLanguages: [String]
    public let minimumTextHeight: Float

    public static let defaultAutoPriorityLanguages: [String] = [
        "en-US", "ja-JP", "zh-Hans", "zh-Hant", "ko-KR", "fr-FR", "de-DE", "es-ES", "ru-RU"
    ]

    /// 選択された翻訳元言語コードとOSが返すサポート言語集合から、絶対に空配列にならない安全な設定を生成する
    public static func resolve(
        sourceLangCode: String,
        supportedByOS: [String]
    ) -> ResolvedVisionOCRSpec {
        let supportedSet = Set(supportedByOS)
        let preferred = preferredVisionLanguages(for: sourceLangCode)
        let filtered: [String]
        if supportedSet.isEmpty {
            filtered = preferred
        } else {
            let intersection = preferred.filter { supportedSet.contains($0) }
            if !intersection.isEmpty {
                filtered = intersection
            } else if supportedSet.contains("en-US") {
                filtered = ["en-US"]
            } else if let firstSupported = supportedByOS.first {
                filtered = [firstSupported]
            } else {
                filtered = ["en-US"]
            }
        }

        return ResolvedVisionOCRSpec(
            pixelFormat: kCVPixelFormatType_32BGRA,
            usesFastLevel: false,
            usesLanguageCorrection: true,
            automaticallyDetectsLanguage: (sourceLangCode == "auto"),
            recognitionLanguages: filtered,
            minimumTextHeight: 0.014
        )
    }

    public static func preferredVisionLanguages(for code: String) -> [String] {
        switch code {
        case "en": return ["en-US", "ja-JP"]
        case "ja": return ["ja-JP", "en-US"]
        case "zh-Hans": return ["zh-Hans", "en-US"]
        case "zh-Hant": return ["zh-Hant", "en-US"]
        case "ko": return ["ko-KR", "en-US"]
        case "fr": return ["fr-FR", "en-US"]
        case "de": return ["de-DE", "en-US"]
        case "es": return ["es-ES", "en-US"]
        case "ru": return ["ru-RU", "en-US"]
        default: return defaultAutoPriorityLanguages
        }
    }
}
