import Foundation
import CoreGraphics

/// 画面翻訳の表示・抽出モード
public enum TranslationDisplayMode: String, CaseIterable, Equatable, Sendable, Identifiable {
    case cardList = "cards"
    case inPlace = "inplace"
    case cursorSpot = "spot"

    public var id: String { rawValue }

    public var shortName: String {
        switch self {
        case .cardList: return "カード"
        case .inPlace: return "直接重畳"
        case .cursorSpot: return "近傍のみ"
        }
    }

    public var menuTitle: String {
        switch self {
        case .cardList:
            return "窓内カード表示（まとまり別＋ホバー枠連動）"
        case .inPlace:
            return "ゲーム画面へ直接重畳（クリック透過・カーソル退避）"
        case .cursorSpot:
            return "マウスカーソル近傍のまとまりのみ翻訳"
        }
    }

    public func nextMode() -> TranslationDisplayMode {
        let all = Self.allCases
        guard let idx = all.firstIndex(of: self) else { return .cardList }
        return all[(idx + 1) % all.count]
    }
}

/// OCRと空間クラスタリングを通過した1つの「文章のまとまり」（正規化座標は 0.0〜1.0、上端原点 topY=0）
public struct ExtractedTextCluster: Equatable, Sendable {
    public let text: String
    public let semanticCore: String
    public let criticalSignature: String
    public let averageConfidence: Float
    public let minX: Double
    public let maxX: Double
    public let topY: Double
    public let bottomY: Double

    public init(
        text: String,
        averageConfidence: Float,
        minX: Double,
        maxX: Double,
        topY: Double,
        bottomY: Double
    ) {
        let norm = TextStabilityGate.normalize(text)
        self.text = norm
        self.semanticCore = TextStabilityGate.semanticCore(norm)
        self.criticalSignature = TextStabilityGate.criticalSignature(norm)
        self.averageConfidence = averageConfidence
        self.minX = minX
        self.maxX = maxX
        self.topY = topY
        self.bottomY = bottomY
    }

    public var midX: Double { (minX + maxX) * 0.5 }
    public var midY: Double { (topY + bottomY) * 0.5 }
    public var normWidth: Double { max(0.001, maxX - minX) }
    public var normHeight: Double { max(0.001, bottomY - topY) }

    /// 正規化点（上端原点 0.0〜1.0）からこのクラスタ矩形までの最短ユークリッド距離（内部なら 0）
    public func distanceToNormalizedPoint(x: Double, y: Double) -> Double {
        let dx: Double
        if x < minX {
            dx = minX - x
        } else if x > maxX {
            dx = x - maxX
        } else {
            dx = 0
        }

        let dy: Double
        if y < topY {
            dy = topY - y
        } else if y > bottomY {
            dy = y - bottomY
        } else {
            dy = 0
        }
        return (dx * dx + dy * dy).squareRoot()
    }
}

/// キャプチャ領域（AppKit左下原点グローバル座標）とクラスタ正規化座標（上端原点）の相互変換ユーティリティ
public enum ClusterGeometry {
    /// クラスタの正規化矩形を AppKit グローバルスクリーン座標（左下原点）の CGRect に変換する
    public static func globalRect(for cluster: ExtractedTextCluster, in captureGlobalRect: CGRect) -> CGRect {
        let w = captureGlobalRect.width
        let h = captureGlobalRect.height
        let x = captureGlobalRect.minX + CGFloat(cluster.minX) * w
        let boxW = max(24.0, CGFloat(cluster.maxX - cluster.minX) * w)
        let globalMaxY = captureGlobalRect.maxY - CGFloat(cluster.topY) * h
        let globalMinY = captureGlobalRect.maxY - CGFloat(cluster.bottomY) * h
        let boxH = max(18.0, globalMaxY - globalMinY)
        return CGRect(x: x, y: globalMaxY - boxH, width: boxW, height: boxH)
    }

    /// AppKit グローバルマウス座標（左下原点）をキャプチャ領域内の正規化座標（上端原点 0..1）へ変換する。
    /// 領域から大きく外れている（周囲 8% マージン外）場合は nil を返す。
    public static func normalizedCursorPoint(
        globalMousePoint: CGPoint,
        in captureGlobalRect: CGRect,
        marginRatio: CGFloat = 0.08
    ) -> (x: Double, y: Double)? {
        guard captureGlobalRect.width >= 16, captureGlobalRect.height >= 16 else { return nil }
        let expanded = captureGlobalRect.insetBy(
            dx: -captureGlobalRect.width * marginRatio,
            dy: -captureGlobalRect.height * marginRatio
        )
        guard expanded.contains(globalMousePoint) else { return nil }

        let rawX = Double((globalMousePoint.x - captureGlobalRect.minX) / captureGlobalRect.width)
        let rawTopY = Double((captureGlobalRect.maxY - globalMousePoint.y) / captureGlobalRect.height)
        return (
            x: max(-0.1, min(1.1, rawX)),
            y: max(-0.1, min(1.1, rawTopY))
        )
    }

    /// インプレースチップに対してマウスカーソルが接近・重畳しているか（裏のUIを見せるため透過退避すべきか）を判定する
    public static func shouldFadeChipForCursor(
        chipGlobalRect: CGRect,
        globalMousePoint: CGPoint,
        hoverPadding: CGFloat = 14.0
    ) -> Bool {
        chipGlobalRect.insetBy(dx: -hoverPadding, dy: -hoverPadding).contains(globalMousePoint)
    }
}

/// 複数クラスタの既訳キャッシュ照合・スポット絞り込み・カーソル優先順位付けを行うプランナ
public enum ClusterTranslationPlanner {
    public struct CachedEntry: Equatable, Sendable {
        public let sourceText: String
        public let semanticCore: String
        public let criticalSignature: String
        public let translatedText: String

        public init(sourceText: String, translatedText: String) {
            let norm = TextStabilityGate.normalize(sourceText)
            self.sourceText = norm
            self.semanticCore = TextStabilityGate.semanticCore(norm)
            self.criticalSignature = TextStabilityGate.criticalSignature(norm)
            self.translatedText = translatedText
        }
    }

    public static let maxDisplayClusters: Int = 8

    /// 表示モードとマウス位置に応じて、画面に表示すべき対象クラスタ群（読み順維持）を抽出する
    public static func selectActiveClusters(
        from candidates: [ExtractedTextCluster],
        mode: TranslationDisplayMode,
        normalizedCursor: (x: Double, y: Double)?
    ) -> [ExtractedTextCluster] {
        guard !candidates.isEmpty else { return [] }

        if mode == .cursorSpot {
            if let cursor = normalizedCursor {
                // カーソルが乗っている、または最も近いクラスタ1件のみを選択
                if let nearest = candidates.min(by: { a, b in
                    let da = a.distanceToNormalizedPoint(x: cursor.x, y: cursor.y)
                    let db = b.distanceToNormalizedPoint(x: cursor.x, y: cursor.y)
                    if abs(da - db) > 0.005 { return da < db }
                    return a.text.count > b.text.count
                }) {
                    let dist = nearest.distanceToNormalizedPoint(x: cursor.x, y: cursor.y)
                    if dist <= 0.18 {
                        return [nearest]
                    }
                }
            }
            // カーソルが監視範囲外にある場合は、最も情報量の多いメインクラスタ1件を選ぶ
            if let primary = candidates.max(by: { $0.text.count < $1.text.count }) {
                return [primary]
            }
            return []
        }

        // カード表示・インプレース重畳: 多すぎる細かいラベルで溢れないよう最大8件に絞る（読み順維持）
        if candidates.count <= maxDisplayClusters {
            return candidates
        }
        let indexed = Array(candidates.enumerated())
        let topIndices = Set(
            indexed
                .sorted { $0.element.text.count > $1.element.text.count }
                .prefix(maxDisplayClusters)
                .map(\.offset)
        )
        return indexed.filter { topIndices.contains($0.offset) }.map(\.element)
    }

    /// 過去の翻訳済みエントリ群から完全一致または高精度ファジー一致（スクロール時の1文字OCR揺れ吸収）を探す
    public static func findCachedTranslation(
        for cluster: ExtractedTextCluster,
        in history: [CachedEntry]
    ) -> String? {
        guard !cluster.semanticCore.isEmpty else { return nil }
        // 新しいエントリを優先して逆順走査
        for entry in history.reversed() {
            guard entry.criticalSignature == cluster.criticalSignature else { continue }
            if entry.semanticCore == cluster.semanticCore {
                return entry.translatedText
            }
            let threshold = (cluster.semanticCore.count < 40) ? 0.89 : 0.91
            if cluster.semanticCore.count >= 14
                && TextStabilityGate.similarity(cluster.semanticCore, entry.semanticCore) >= threshold {
                return entry.translatedText
            }
        }
        return nil
    }

    /// 未翻訳クラスタのインデックス配列を、マウスカーソル近傍を最優先にしつつ上から順に並べ替える
    public static func prioritizedIndices(
        clusters: [ExtractedTextCluster],
        untranslatedIndices: [Int],
        normalizedCursor: (x: Double, y: Double)?
    ) -> [Int] {
        guard untranslatedIndices.count > 1, let cursor = normalizedCursor else {
            return untranslatedIndices
        }
        return untranslatedIndices.sorted { ia, ib in
            let da = clusters[ia].distanceToNormalizedPoint(x: cursor.x, y: cursor.y)
            let db = clusters[ib].distanceToNormalizedPoint(x: cursor.x, y: cursor.y)
            // カーソルから明確に近い（距離差 0.04 以上、かつ距離 0.25 以内）クラスタを先頭へ引き上げる
            let aNear = da <= 0.25
            let bNear = db <= 0.25
            if aNear != bNear {
                return aNear
            }
            if aNear && bNear && abs(da - db) > 0.04 {
                return da < db
            }
            return ia < ib
        }
    }
}
