import AppKit
import QuartzCore

/// 02_UIデザイン方向に準拠した最小限のデザイントークン（8ptグリッド・余計な装飾なし）
enum DesignTokens {
    // MARK: - Grid & Geometry
    static let panelDefaultWidth: CGFloat = 360.0
    static let panelMinWidth: CGFloat = 240.0
    static let panelMaxWidth: CGFloat = 640.0
    static let padding: CGFloat = 16.0
    static let spacingSmall: CGFloat = 8.0
    static let spacingMedium: CGFloat = 12.0
    static let cornerRadius: CGFloat = 12.0
    static let hairlineThickness: CGFloat = 1.0
    static let bracketLength: CGFloat = 10.0

    // MARK: - Colors (地 #12161C + 白濃度2段 + アクセント #4DA892 1色のみ)
    static let surfaceCharcoal = NSColor(calibratedRed: 0x12 / 255.0, green: 0x16 / 255.0, blue: 0x1C / 255.0, alpha: 0.80)
    static let innerHairline = NSColor(calibratedWhite: 1.0, alpha: 0.09)
    static let dividerHairline = NSColor(calibratedWhite: 1.0, alpha: 0.10)

    /// 確定訳（白 92%）
    static let textCommitted = NSColor(calibratedWhite: 1.0, alpha: 0.92)
    /// 暫定訳・直前履歴（白 45%）
    static let textSecondary = NSColor(calibratedWhite: 1.0, alpha: 0.45)

    /// 唯一のアクセント: 滲んだエメラルドグリーン (#4DA892)
    static let accentEmerald = NSColor(calibratedRed: 0x4D / 255.0, green: 0xA8 / 255.0, blue: 0x92 / 255.0, alpha: 1.0)
    static let selectionScrim = NSColor(calibratedRed: 0.04, green: 0.06, blue: 0.08, alpha: 0.42)

    // MARK: - Typography (システムフォントのみ: SF Pro / ヒラギノ角ゴ)
    static let bodyFontSize: CGFloat = 15.0
    static let bodyLineHeightMultiple: CGFloat = 1.55
    static let previousFontSize: CGFloat = 13.0
    static let roleLabelFontSize: CGFloat = 10.0
    static let roleLabelTracking: CGFloat = 1.8 // 0.18em at 10pt

    // MARK: - Motion (opacity と transform のみ、120〜240ms、easeOutQuint)
    static let durationFast: CFTimeInterval = 0.16
    static let durationStandard: CFTimeInterval = 0.22
    static var easeOutQuint: CAMediaTimingFunction {
        CAMediaTimingFunction(controlPoints: 0.22, 1.0, 0.36, 1.0)
    }
}
