import Foundation
import NaturalLanguage

/// Vision等のOCRエンジンから渡される正規化座標付きテキスト観測値（0.0〜1.0、左下原点）
public struct OCRTextObservation: Equatable, Sendable {
    public let text: String
    public let confidence: Float
    public let normX: Double
    public let normY: Double
    public let normWidth: Double
    public let normHeight: Double

    public init(
        text: String,
        confidence: Float,
        normX: Double,
        normY: Double,
        normWidth: Double,
        normHeight: Double
    ) {
        self.text = text
        self.confidence = confidence
        self.normX = normX
        self.normY = normY
        self.normWidth = normWidth
        self.normHeight = normHeight
    }
}

/// 翻訳対象外と判定された理由（決定的ゲートおよび言語確率ゲート）
public enum SkipReason: String, Equatable, Sendable {
    case empty
    case lowConfidenceNoise
    case symbolsOrPunctuationOnly
    case numericOrStatsOnly
    case keybindOrHUDControl
    case codeOrIdentifier
    case scriptMismatch
    case alreadyTargetLanguage
    case lowLanguageConfidence

    /// 本文待機欄に表示する説明
    public var detailJaText: String {
        switch self {
        case .empty:
            return "テキスト未検出"
        case .lowConfidenceNoise:
            return "背景ノイズ・低信頼OCRを除外"
        case .symbolsOrPunctuationOnly:
            return "記号・装飾文字のみのため対象外"
        case .numericOrStatsOnly:
            return "数値・ステータス表記のため対象外"
        case .keybindOrHUDControl:
            return "キー操作・UIラベルのため対象外"
        case .codeOrIdentifier:
            return "URL・パス・識別子のため対象外"
        case .scriptMismatch:
            return "選択中の翻訳元言語の文字を含まないため対象外"
        case .alreadyTargetLanguage:
            return "すでに翻訳先の言語のため対象外"
        case .lowLanguageConfidence:
            return "言語として成立しない断片のため対象外"
        }
    }

    /// ヘッダーに控えめに表示する短い状態ラベル
    public var shortBadgeJaText: String {
        switch self {
        case .empty:
            return ""
        case .lowConfidenceNoise:
            return "除外: ノイズ"
        case .symbolsOrPunctuationOnly:
            return "除外: 記号"
        case .numericOrStatsOnly:
            return "除外: 数値"
        case .keybindOrHUDControl:
            return "除外: UI操作"
        case .codeOrIdentifier:
            return "除外: 識別子"
        case .scriptMismatch:
            return "除外: 別文字系"
        case .alreadyTargetLanguage:
            return "除外: 翻訳先言語"
        case .lowLanguageConfidence:
            return "除外: 断片"
        }
    }
}

/// 空間フィルタ後の抽出結果（まとまり配列 clusters と全文結合 text の両方を保持）
public struct SpatialExtractionResult: Equatable, Sendable {
    public let text: String
    public let clusters: [ExtractedTextCluster]
    public let averageConfidence: Float
    public let keptClusterCount: Int
    public let droppedObservationCount: Int
    public let lastRejectedReason: SkipReason?

    public init(
        text: String,
        clusters: [ExtractedTextCluster] = [],
        averageConfidence: Float,
        keptClusterCount: Int,
        droppedObservationCount: Int,
        lastRejectedReason: SkipReason?
    ) {
        self.text = text
        self.clusters = clusters
        self.averageConfidence = averageConfidence
        self.keptClusterCount = keptClusterCount
        self.droppedObservationCount = droppedObservationCount
        self.lastRejectedReason = lastRejectedReason
    }
}

/// 多段翻訳要否ゲートの最終判定
public enum GateDecision: Equatable, Sendable {
    case translate(cleanedText: String, resolvedSourceCode: String, languageConfidence: Double)
    case skip(reason: SkipReason)
}

/// 第1段: Vision OCR観測値の信頼度フィルタ・横空隙10%分割・装飾アーティファクト除去・周辺HUDバッジ分離
/// 追加ヒープ常駐ゼロ、1フレームあたり数十マイクロ秒で動作する。
public enum OCRSpatialFilter {
    /// 標準文字列（3文字以上）の最低OCR信頼度（roec picture_text.py の 0.5 基準に整合）
    public static let standardMinConfidence: Float = 0.50
    /// 1〜2文字の短文に対する厳格OCR信頼度（背景模様の誤読 |, l, =, x 等を遮断）
    public static let shortTokenMinConfidence: Float = 0.72
    /// 同一行内でも別UI要素として切り離す横方向空隙比（領域幅の10%: roec nearby_groups 準拠）
    public static let horizontalSplitGapRatio: Double = 0.10

    private static let validTwoLetterWords: Set<String> = [
        "in", "on", "at", "to", "of", "is", "it", "as", "be", "by", "or",
        "no", "so", "up", "we", "he", "my", "me", "us", "do", "go", "if", "am", "an"
    ]

    private struct PositionedItem {
        let text: String
        let confidence: Float
        let minX: Double
        let maxX: Double
        let topY: Double      // 上端原点 (0.0 = 上端, 1.0 = 下端)
        let bottomY: Double
        var centerY: Double { (topY + bottomY) * 0.5 }
        var height: Double { max(0.001, bottomY - topY) }
    }

    /// 見出し横の唐草装飾模様などが "co", "oo", "cc", "sc" 等の短い小文字片として誤読されたアイテムか判定する
    private static func isLikelyTrailingOrnament(_ item: PositionedItem, gapFromPrev: Double) -> Bool {
        let trimmed = item.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count <= 2 else { return false }
        let scalars = Array(trimmed.unicodeScalars)
        // CJK文字（漢字1〜2文字の単語など）は正当な語彙なので保持
        if scalars.contains(where: { TranslatabilityGate.isCJKScalar($0) }) {
            return false
        }
        let lower = trimmed.lowercased()
        // 唐草模様の典型誤読（"co", "oo", "cc", "ce", "eo", "sc", "cs" 等）で前の語から少し離れている場合を除外
        if gapFromPrev >= max(0.018, item.height * 0.55) {
            if !validTwoLetterWords.contains(lower) {
                return true
            }
            if item.confidence < 0.88 {
                return true
            }
        }
        return false
    }

    private struct HorizontalChunk {
        var items: [PositionedItem]
        var minX: Double
        var maxX: Double
        var topY: Double
        var bottomY: Double
        var midX: Double { (minX + maxX) * 0.5 }
        var height: Double { max(0.001, bottomY - topY) }

        var joinedText: String {
            guard let first = items.first else { return "" }
            var out = first.text
            for idx in 1..<items.count {
                let prev = items[idx - 1]
                let cur = items[idx]
                let gap = cur.minX - prev.maxX
                if OCRSpatialFilter.isLikelyTrailingOrnament(cur, gapFromPrev: gap) && idx == items.count - 1 {
                    continue
                }
                if gap < 0.025 && Self.endsWithCJK(prev.text) && Self.startsWithCJK(cur.text) {
                    out += cur.text
                } else {
                    out += " " + cur.text
                }
            }
            return out
        }

        var weightedConfidence: Float {
            var totalWeight: Float = 0
            var sum: Float = 0
            for it in items {
                let w = Float(max(1, it.text.count))
                sum += it.confidence * w
                totalWeight += w
            }
            return totalWeight > 0 ? (sum / totalWeight) : 0
        }

        private static func endsWithCJK(_ s: String) -> Bool {
            guard let sc = s.unicodeScalars.last else { return false }
            return TranslatabilityGate.isCJKScalar(sc)
        }

        private static func startsWithCJK(_ s: String) -> Bool {
            guard let sc = s.unicodeScalars.first else { return false }
            return TranslatabilityGate.isCJKScalar(sc)
        }
    }

    private struct TextCluster {
        var lines: [HorizontalChunk]
        var minX: Double
        var maxX: Double
        var topY: Double
        var bottomY: Double

        var text: String {
            guard let first = lines.first else { return "" }
            var out = first.joinedText
            var prevLine = first
            for line in lines.dropFirst() {
                let nextText = line.joinedText
                if Self.shouldSoftJoin(prevText: out, prevLine: prevLine, nextText: nextText, nextLine: line) {
                    if Self.endsWithCJK(out) && Self.startsWithCJK(nextText) {
                        out += nextText
                    } else {
                        out += " " + nextText
                    }
                } else {
                    out += "\n" + nextText
                }
                prevLine = line
            }
            return out
        }

        private static func shouldSoftJoin(
            prevText: String,
            prevLine: HorizontalChunk,
            nextText: String,
            nextLine: HorizontalChunk
        ) -> Bool {
            let trimmedPrev = prevText.trimmingCharacters(in: .whitespacesAndNewlines)
            let trimmedNext = nextText.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmedPrev.isEmpty, !trimmedNext.isEmpty else { return false }

            // フォントサイズ（行高さ）が大きく異なる場合（例: キャラ名見出しと本文）は改行を維持する
            let maxH = max(prevLine.height, nextLine.height)
            let minH = min(prevLine.height, nextLine.height)
            if maxH > 0, (minH / maxH) < 0.72 {
                return false
            }

            // 文末記号や閉じ括弧で終わっている場合は段落・文の区切りとして改行を維持する
            if let lastChar = trimmedPrev.last {
                let terminalChars: Set<Character> = [".", "!", "?", "。", "！", "？", ":", "：", ";", "；", "…", "」", "』", "】", ")"]
                if terminalChars.contains(lastChar) {
                    return false
                }
            }

            // 次行が箇条書き記号で始まる場合は改行を維持する
            if let firstChar = trimmedNext.first {
                let bulletChars: Set<Character> = ["•", "・", "-", "*", "▪", "▫", "◆", "◇", "▶"]
                if bulletChars.contains(firstChar) {
                    return false
                }
            }

            return true
        }

        private static func endsWithCJK(_ s: String) -> Bool {
            guard let sc = s.unicodeScalars.last else { return false }
            return TranslatabilityGate.isCJKScalar(sc)
        }

        private static func startsWithCJK(_ s: String) -> Bool {
            guard let sc = s.unicodeScalars.first else { return false }
            return TranslatabilityGate.isCJKScalar(sc)
        }

        var charCount: Int {
            lines.reduce(0) { $0 + $1.joinedText.count }
        }

        var weightedConfidence: Float {
            var totalW: Float = 0
            var sum: Float = 0
            for line in lines {
                let w = Float(max(1, line.joinedText.count))
                sum += line.weightedConfidence * w
                totalW += w
            }
            return totalW > 0 ? (sum / totalW) : 0
        }
    }

    public static func extractPrimaryText(
        from observations: [OCRTextObservation],
        sourceLangCode: String = "auto",
        targetLangCode: String = "ja"
    ) -> SpatialExtractionResult {
        guard !observations.isEmpty else {
            return SpatialExtractionResult(
                text: "",
                averageConfidence: 0,
                keptClusterCount: 0,
                droppedObservationCount: 0,
                lastRejectedReason: .empty
            )
        }

        var validItems: [PositionedItem] = []
        validItems.reserveCapacity(observations.count)
        var dropped = 0
        var lastReject: SkipReason?

        for obs in observations {
            let trimmed = obs.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty {
                dropped += 1
                continue
            }

            // 1文字の句読点・記号ノイズを即時除外（roec picture_text.py 準拠）
            let visibleScalars = trimmed.unicodeScalars.filter { !$0.properties.isWhitespace }
            if visibleScalars.count == 1, let only = visibleScalars.first {
                if !only.properties.isAlphabetic && !only.properties.isIdeographic && only.properties.numericType == nil {
                    dropped += 1
                    lastReject = .symbolsOrPunctuationOnly
                    continue
                }
            }

            // 文字長に応じたOCR信頼度下限
            let minConf = (visibleScalars.count <= 2) ? shortTokenMinConfidence : standardMinConfidence
            if obs.confidence < minConf {
                dropped += 1
                lastReject = .lowConfidenceNoise
                continue
            }

            // 縦線・罫線の誤読反復パターン（"||||", "Il1Il", "====" 等）を遮断
            if TranslatabilityGate.isRepetitiveOCRArtifact(trimmed) {
                dropped += 1
                lastReject = .lowConfidenceNoise
                continue
            }

            let topY = max(0.0, min(1.0, 1.0 - (obs.normY + obs.normHeight)))
            let bottomY = max(topY + 0.001, min(1.0, 1.0 - obs.normY))
            let minX = max(0.0, min(1.0, obs.normX))
            let maxX = max(minX + 0.001, min(1.0, obs.normX + obs.normWidth))

            validItems.append(
                PositionedItem(
                    text: trimmed,
                    confidence: obs.confidence,
                    minX: minX,
                    maxX: maxX,
                    topY: topY,
                    bottomY: bottomY
                )
            )
        }

        guard !validItems.isEmpty else {
            return SpatialExtractionResult(
                text: "",
                averageConfidence: 0,
                keptClusterCount: 0,
                droppedObservationCount: dropped,
                lastRejectedReason: lastReject ?? .lowConfidenceNoise
            )
        }

        // 上から下、左から右へ整列して行グループ化
        validItems.sort { a, b in
            if abs(a.centerY - b.centerY) > min(a.height, b.height) * 0.55 {
                return a.centerY < b.centerY
            }
            return a.minX < b.minX
        }

        var rowBuckets: [(centerY: Double, height: Double, items: [PositionedItem])] = []
        for item in validItems {
            if let idx = rowBuckets.indices.reversed().first(where: { i in
                abs(rowBuckets[i].centerY - item.centerY) <= min(rowBuckets[i].height, item.height) * 0.55
            }) {
                rowBuckets[idx].items.append(item)
                let count = Double(rowBuckets[idx].items.count)
                rowBuckets[idx].centerY = ((rowBuckets[idx].centerY * (count - 1.0)) + item.centerY) / count
                rowBuckets[idx].height = max(rowBuckets[idx].height, item.height)
            } else {
                rowBuckets.append((centerY: item.centerY, height: item.height, items: [item]))
            }
        }

        rowBuckets.sort { $0.centerY < $1.centerY }

        // 各行内で横空隙が領域幅の10%を超える箇所を別チャンクに分割
        var horizontalChunks: [HorizontalChunk] = []
        for bucket in rowBuckets {
            let sortedLine = bucket.items.sorted { $0.minX < $1.minX }
            var currentGroup: [PositionedItem] = []
            var prevRight: Double?

            for item in sortedLine {
                if let right = prevRight, (item.minX - right) > horizontalSplitGapRatio {
                    if let chunk = makeChunk(from: currentGroup) {
                        horizontalChunks.append(chunk)
                    }
                    currentGroup = [item]
                    prevRight = item.maxX
                } else {
                    currentGroup.append(item)
                    prevRight = max(prevRight ?? item.maxX, item.maxX)
                }
            }
            if let chunk = makeChunk(from: currentGroup) {
                horizontalChunks.append(chunk)
            }
        }

        // 縦方向に近接し、文字サイズ（高さ比 >= 0.70）かつ水平範囲が揃っているチャンクを同一段落クラスタに束ねる
        // （大見出しキャラ名と小文字本文のように文字高さが大きく異なる要素は別クラスタへ分離する）
        var clusters: [TextCluster] = []
        for chunk in horizontalChunks {
            if let idx = clusters.indices.last(where: { i in
                guard let lastLine = clusters[i].lines.last else { return false }
                let maxH = max(lastLine.height, chunk.height)
                let minH = min(lastLine.height, chunk.height)
                if maxH > 0, (minH / maxH) < 0.70 {
                    return false
                }
                let vGap = chunk.topY - lastLine.bottomY
                let maxAllowedGap = maxH * 1.45
                guard vGap >= -minH * 0.4 && vGap <= maxAllowedGap else {
                    return false
                }
                let overlap = min(clusters[i].maxX, chunk.maxX) - max(clusters[i].minX, chunk.minX)
                let leftAligned = abs(clusters[i].minX - chunk.minX) <= 0.12
                let centerAligned = abs(((clusters[i].minX + clusters[i].maxX) * 0.5) - chunk.midX) <= 0.12
                return overlap > 0 || leftAligned || centerAligned
            }) {
                clusters[idx].lines.append(chunk)
                clusters[idx].minX = min(clusters[idx].minX, chunk.minX)
                clusters[idx].maxX = max(clusters[idx].maxX, chunk.maxX)
                clusters[idx].topY = min(clusters[idx].topY, chunk.topY)
                clusters[idx].bottomY = max(clusters[idx].bottomY, chunk.bottomY)
            } else {
                clusters.append(
                    TextCluster(
                        lines: [chunk],
                        minX: chunk.minX,
                        maxX: chunk.maxX,
                        topY: chunk.topY,
                        bottomY: chunk.bottomY
                    )
                )
            }
        }

        // クラスタ単位で明らかな非翻訳要素（FPS/HP数値・キー操作・記号・URL等）を除去し、本文への混入を防ぐ
        var survivingClusters: [TextCluster] = []
        for cluster in clusters {
            let candidate = cluster.text
            if let structuralReject = TranslatabilityGate.structuralRejectReason(
                for: candidate,
                sourceLangCode: sourceLangCode,
                targetLangCode: targetLangCode
            ) {
                dropped += cluster.lines.reduce(0) { $0 + $1.items.count }
                lastReject = structuralReject
                continue
            }
            survivingClusters.append(cluster)
        }

        // 本文クラスタが存在する場合、四隅に離れた孤立HUDボタン（"AUTO", "SKIP", "LOG" 等）や
        // 装飾模様の誤読による1〜2文字の孤立断片を除外する
        if survivingClusters.count >= 2 {
            let maxChars = survivingClusters.map(\.charCount).max() ?? 0
            if maxChars >= 10 {
                survivingClusters = survivingClusters.filter { cl in
                    let trimmedCluster = cl.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    if TranslatabilityGate.isPeripheralHUDChromeBadge(trimmedCluster) && cl.charCount * 3 < maxChars {
                        dropped += 1
                        return false
                    }
                    if cl.charCount <= 2 && maxChars >= 20 {
                        dropped += 1
                        return false
                    }
                    return true
                }
            }
        }

        guard !survivingClusters.isEmpty else {
            return SpatialExtractionResult(
                text: "",
                clusters: [],
                averageConfidence: 0,
                keptClusterCount: 0,
                droppedObservationCount: dropped,
                lastRejectedReason: lastReject ?? .numericOrStatsOnly
            )
        }

        // 上から下へ自然な読み順で整列
        survivingClusters.sort { a, b in
            if abs(a.topY - b.topY) > 0.04 {
                return a.topY < b.topY
            }
            return a.minX < b.minX
        }

        var extractedClusters: [ExtractedTextCluster] = []
        extractedClusters.reserveCapacity(survivingClusters.count)
        let finalLines = survivingClusters.map(\.text)
        var totalWeight: Float = 0
        var confSum: Float = 0
        for cl in survivingClusters {
            let cleanClusterText = cl.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !cleanClusterText.isEmpty else { continue }
            let w = Float(max(1, cl.charCount))
            confSum += cl.weightedConfidence * w
            totalWeight += w
            extractedClusters.append(
                ExtractedTextCluster(
                    text: cleanClusterText,
                    averageConfidence: cl.weightedConfidence,
                    minX: cl.minX,
                    maxX: cl.maxX,
                    topY: cl.topY,
                    bottomY: cl.bottomY
                )
            )
        }

        return SpatialExtractionResult(
            text: finalLines.joined(separator: "\n"),
            clusters: extractedClusters,
            averageConfidence: totalWeight > 0 ? (confSum / totalWeight) : 0,
            keptClusterCount: extractedClusters.count,
            droppedObservationCount: dropped,
            lastRejectedReason: nil
        )
    }

    private static func makeChunk(from items: [PositionedItem]) -> HorizontalChunk? {
        guard let first = items.first else { return nil }
        var minX = first.minX
        var maxX = first.maxX
        var topY = first.topY
        var bottomY = first.bottomY
        for it in items.dropFirst() {
            minX = min(minX, it.minX)
            maxX = max(maxX, it.maxX)
            topY = min(topY, it.topY)
            bottomY = max(bottomY, it.bottomY)
        }
        return HorizontalChunk(items: items, minX: minX, maxX: maxX, topY: topY, bottomY: bottomY)
    }
}

/// 第2・第3段: 決定的構造・文字体系ゲート ＋ NaturalLanguage 言語確率分布・語彙トークンゲート
public enum TranslatabilityGate {

    private static let statAndUnitTokens: Set<String> = [
        "fps", "ms", "ping", "hp", "mp", "sp", "ap", "ep", "cp", "lp", "vp",
        "exp", "xp", "lv", "lvl", "atk", "def", "matk", "mdef", "spd", "crit", "dps",
        "kb", "mb", "gb", "tb", "kbps", "mbps", "hz", "khz", "mhz", "ghz",
        "px", "pt", "cm", "mm", "km", "kg", "sec", "min", "hr", "am", "pm", "ver", "v"
    ]

    private static let standaloneHardwareKeys: Set<String> = [
        "esc", "tab", "caps", "shift", "ctrl", "control", "alt", "opt", "option",
        "cmd", "command", "fn", "enter", "return", "space", "bksp", "del", "delete",
        "ins", "home", "end", "pgup", "pgdn",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "l1", "l2", "l3", "r1", "r2", "r3", "lb", "rb", "lt", "rt"
    ]

    private static let peripheralHUDChromeBadges: Set<String> = [
        "auto", "skip", "log", "menu", "pause", "paused", "hud", "ui", "rec", "live", "mute"
    ]

    private struct ScriptProfile {
        var totalVisible: Int = 0
        var letterCount: Int = 0
        var digitCount: Int = 0
        var symbolPunctCount: Int = 0
        var kanaCount: Int = 0
        var hanCount: Int = 0
        var hangulCount: Int = 0
        var cyrillicCount: Int = 0
        var latinCount: Int = 0
    }

    private static let recognizerLock = NSLock()
    private static let sharedRecognizer = NLLanguageRecognizer()

    private static func queryHypotheses(for sample: String) -> [NLLanguage: Double] {
        let clipped = (sample.count > 240) ? String(sample.prefix(240)) : sample
        recognizerLock.lock()
        defer { recognizerLock.unlock() }
        sharedRecognizer.reset()
        sharedRecognizer.processString(clipped)
        return sharedRecognizer.languageHypotheses(withMaximum: 5)
    }

    /// 完全な多段判定を実行し、翻訳すべきテキストと解決済み翻訳元言語コード、またはスキップ理由を返す
    public static func evaluate(
        rawText: String,
        selectedSourceCode: String,
        targetCode: String
    ) -> GateDecision {
        let normalized = TextStabilityGate.normalize(rawText)
        guard !normalized.isEmpty else {
            return .skip(reason: .empty)
        }

        if let structuralReason = structuralRejectReason(
            for: normalized,
            sourceLangCode: selectedSourceCode,
            targetLangCode: targetCode
        ) {
            return .skip(reason: structuralReason)
        }

        let profile = analyzeScripts(normalized)

        let wordTokens = extractLexicalWordTokens(normalized)
        if wordTokens.isEmpty {
            return .skip(reason: .symbolsOrPunctuationOnly)
        }

        if profile.kanaCount == 0 && profile.hanCount == 0 && profile.hangulCount == 0 && profile.cyrillicCount == 0 {
            if isLatinOCRGibberish(tokens: wordTokens, totalLetters: profile.letterCount) {
                return .skip(reason: .lowLanguageConfidence)
            }
        }

        if selectedSourceCode != "auto" {
            if selectedSourceCode == targetCode {
                return .skip(reason: .alreadyTargetLanguage)
            }
            if targetCode == "ja" || targetCode == "ko" || targetCode == "ru" {
                return .translate(cleanedText: normalized, resolvedSourceCode: selectedSourceCode, languageConfidence: 0.95)
            }
            let hypotheses = queryHypotheses(for: normalized)
            if isPredominantlyLanguage(targetCode, profile: profile, hypotheses: hypotheses) {
                return .skip(reason: .alreadyTargetLanguage)
            }
            let conf = hypothesisProbability(for: selectedSourceCode, in: hypotheses, profile: profile)
            return .translate(cleanedText: normalized, resolvedSourceCode: selectedSourceCode, languageConfidence: conf)
        }

        if profile.kanaCount > 0 && Double(profile.kanaCount + profile.hanCount) / Double(profile.letterCount) >= 0.35 {
            return (targetCode == "ja")
                ? .skip(reason: .alreadyTargetLanguage)
                : .translate(cleanedText: normalized, resolvedSourceCode: "ja", languageConfidence: 0.95)
        }
        if profile.hangulCount > 0 && Double(profile.hangulCount) / Double(profile.letterCount) >= 0.35 {
            return (targetCode == "ko")
                ? .skip(reason: .alreadyTargetLanguage)
                : .translate(cleanedText: normalized, resolvedSourceCode: "ko", languageConfidence: 0.95)
        }
        if profile.cyrillicCount > 0 && Double(profile.cyrillicCount) / Double(profile.letterCount) >= 0.35 {
            return (targetCode == "ru")
                ? .skip(reason: .alreadyTargetLanguage)
                : .translate(cleanedText: normalized, resolvedSourceCode: "ru", languageConfidence: 0.92)
        }

        let hypotheses = queryHypotheses(for: normalized)
        guard let (resolvedCode, confidence) = resolveAutoLanguage(profile: profile, hypotheses: hypotheses, targetCode: targetCode) else {
            return .skip(reason: .lowLanguageConfidence)
        }

        if resolvedCode == targetCode {
            return .skip(reason: .alreadyTargetLanguage)
        }

        return .translate(
            cleanedText: normalized,
            resolvedSourceCode: resolvedCode,
            languageConfidence: confidence
        )
    }

    /// クラスタ単位でも呼べる高速・決定的な構造棄却判定（意味推測による誤棄却を避け、100%翻訳不要な形式のみを弾く）
    public static func structuralRejectReason(
        for text: String,
        sourceLangCode: String,
        targetLangCode: String
    ) -> SkipReason? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            return .empty
        }

        if isRepetitiveOCRArtifact(trimmed) {
            return .lowConfidenceNoise
        }

        let profile = analyzeScripts(trimmed)
        if profile.letterCount == 0 {
            return (profile.digitCount > 0) ? .numericOrStatsOnly : .symbolsOrPunctuationOnly
        }

        // 文字の大半が記号・数字で埋まっているOCR崩れ（例: "*** 12 / 99 === #x"）
        if profile.totalVisible >= 4 && Double(profile.letterCount) / Double(profile.totalVisible) < 0.28 {
            return (profile.digitCount >= profile.symbolPunctCount) ? .numericOrStatsOnly : .symbolsOrPunctuationOnly
        }

        // キー操作表記（"[E]", "⌥⇧R", "Ctrl+C", 単独の "ESC" 等）
        if isKeybindPrompt(trimmed) {
            return .keybindOrHUDControl
        }

        // 数値＋単位/ゲームステータスのみ（"120 FPS", "HP 450/500", "Lv.99", "03:55" 等）
        if isNumericOrStatExpression(trimmed) {
            return .numericOrStatsOnly
        }

        // URL・ファイルパス・CLIフラグ・ハッシュ・コード識別子・ソースコード断片
        if isCodeOrIdentifier(trimmed, profile: profile) {
            return .codeOrIdentifier
        }

        // 文字体系（スクリプト）による翻訳先既訳判定（確実に判定できる固有文字体系）
        if isDefinitelyTargetScript(profile: profile, targetCode: targetLangCode) {
            return .alreadyTargetLanguage
        }

        // 明示された翻訳元言語のスクリプトが一切含まれない場合のノイズ遮断（bquenin/interpreter 方式）
        if sourceLangCode != "auto" && !containsExpectedSourceScript(profile: profile, sourceCode: sourceLangCode) {
            return .scriptMismatch
        }

        return nil
    }

    public static func isPeripheralHUDChromeBadge(_ text: String) -> Bool {
        let lower = text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if peripheralHUDChromeBadges.contains(lower) {
            return true
        }
        let parts = lower.split(whereSeparator: { $0.isWhitespace || $0 == "/" || $0 == "|" })
        if !parts.isEmpty && parts.count <= 3 && parts.allSatisfy({ peripheralHUDChromeBadges.contains(String($0)) }) {
            return true
        }
        return false
    }

    public static func isRepetitiveOCRArtifact(_ text: String) -> Bool {
        let scalars = text.unicodeScalars.filter { !$0.properties.isWhitespace }
        guard scalars.count >= 3 else { return false }

        let unique = Set(scalars)
        if unique.count == 1, let only = scalars.first {
            if only == "|" || only == "l" || only == "I" || only == "1" || only == "_" || only == "=" || only == "-" || only == "." {
                return true
            }
            if scalars.count >= 4 {
                return true
            }
        }

        // Visionが縦格子や枠線を "|lI1!/" の混合として誤読する典型パターン
        let verticalStrokeChars: Set<UnicodeScalar> = ["|", "l", "I", "1", "!", "/", "\\", "[", "]"]
        let strokeHits = scalars.filter { verticalStrokeChars.contains($0) }.count
        if scalars.count >= 3 && Double(strokeHits) / Double(scalars.count) >= 0.80 {
            return true
        }

        return false
    }

    public static func isCJKScalar(_ sc: UnicodeScalar) -> Bool {
        let v = sc.value
        return (0x3040...0x30FF).contains(v)
            || (0x3400...0x4DBF).contains(v)
            || (0x4E00...0x9FFF).contains(v)
            || (0xAC00...0xD7AF).contains(v)
            || (0xFF66...0xFF9F).contains(v)
    }

    // MARK: - Internal Helpers

    private static func analyzeScripts(_ text: String) -> ScriptProfile {
        var p = ScriptProfile()
        for sc in text.unicodeScalars {
            if sc.properties.isWhitespace { continue }
            p.totalVisible += 1
            let v = sc.value

            if (0x3040...0x309F).contains(v) || (0x30A0...0x30FF).contains(v) || (0xFF66...0xFF9F).contains(v) {
                p.letterCount += 1
                p.kanaCount += 1
            } else if (0x4E00...0x9FFF).contains(v) || (0x3400...0x4DBF).contains(v) || (0xF900...0xFAFF).contains(v) {
                p.letterCount += 1
                p.hanCount += 1
            } else if (0xAC00...0xD7AF).contains(v) || (0x1100...0x11FF).contains(v) || (0x3130...0x318F).contains(v) {
                p.letterCount += 1
                p.hangulCount += 1
            } else if (0x0400...0x04FF).contains(v) {
                p.letterCount += 1
                p.cyrillicCount += 1
            } else if sc.properties.isAlphabetic || sc.properties.isIdeographic {
                p.letterCount += 1
                p.latinCount += 1
            } else if sc.properties.numericType != nil || (0x0030...0x0039).contains(v) {
                p.digitCount += 1
            } else {
                p.symbolPunctCount += 1
            }
        }
        return p
    }

    private static func isKeybindPrompt(_ text: String) -> Bool {
        let compact = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = compact.lowercased()

        if standaloneHardwareKeys.contains(lower) {
            return true
        }

        if compact.count >= 3 && compact.count <= 9 {
            let pairs: [(Character, Character)] = [("[", "]"), ("(", ")"), ("<", ">"), ("【", "】")]
            if let first = compact.first, let last = compact.last,
               pairs.contains(where: { $0.0 == first && $0.1 == last }) {
                let inner = String(compact.dropFirst().dropLast()).trimmingCharacters(in: .whitespaces).lowercased()
                if inner.count == 1 || standaloneHardwareKeys.contains(inner) {
                    return true
                }
            }
        }

        let macMods: Set<Character> = ["⌘", "⌥", "⌃", "⇧", "⎋", "⏎", "⌫", "⇥"]
        if compact.count <= 12 && !compact.contains(" ") && compact.contains(where: { macMods.contains($0) }) {
            return true
        }

        if compact.contains("+") && compact.count <= 24 {
            let parts = lower.split(separator: "+").map { $0.trimmingCharacters(in: .whitespaces) }
            if parts.count >= 2 && parts.allSatisfy({ part in
                standaloneHardwareKeys.contains(part) || part.count == 1
            }) {
                return true
            }
        }

        return false
    }

    private static func isNumericOrStatExpression(_ text: String) -> Bool {
        var hasDigit = false
        for sc in text.unicodeScalars {
            if sc.properties.numericType != nil || (0x0030...0x0039).contains(sc.value) {
                hasDigit = true
                break
            }
        }
        guard hasDigit else { return false }

        var currentWord = ""
        var words: [String] = []

        for sc in text.unicodeScalars {
            if sc.properties.isAlphabetic || sc.properties.isIdeographic {
                currentWord.unicodeScalars.append(sc)
            } else {
                if !currentWord.isEmpty {
                    words.append(currentWord.lowercased())
                    currentWord = ""
                }
            }
        }
        if !currentWord.isEmpty {
            words.append(currentWord.lowercased())
        }

        if words.isEmpty {
            return true
        }

        return words.allSatisfy { w in
            statAndUnitTokens.contains(w) || w == "x"
        }
    }

    private static func isCodeOrIdentifier(_ text: String, profile: ScriptProfile) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = trimmed.lowercased()
        let hasNonLatinScript = (profile.kanaCount + profile.hanCount + profile.hangulCount + profile.cyrillicCount) > 0

        if !trimmed.contains(where: { $0.isWhitespace }) {
            if lower.hasPrefix("http://") || lower.hasPrefix("https://") || lower.hasPrefix("ws://")
                || lower.hasPrefix("wss://") || lower.hasPrefix("file://") || lower.hasPrefix("www.") {
                return true
            }
            if trimmed.hasPrefix("/") || trimmed.hasPrefix("~/") || trimmed.hasPrefix("./") || trimmed.hasPrefix("../") {
                return true
            }
            if trimmed.count >= 4, trimmed.dropFirst().hasPrefix(":\\") {
                return true
            }
            if trimmed.hasPrefix("--") && trimmed.count >= 4 {
                return true
            }
            if !hasNonLatinScript {
                if trimmed.contains("_") || trimmed.contains("::") || (trimmed.contains("@") && trimmed.contains(".")) {
                    return true
                }
                let dotParts = trimmed.split(separator: ".")
                if dotParts.count >= 2 && trimmed.count >= 5 && dotParts.allSatisfy({ !$0.isEmpty }) {
                    if !trimmed.hasSuffix(".") {
                        return true
                    }
                }
                if lower.hasPrefix("0x") && trimmed.count >= 5 {
                    return true
                }
                if trimmed.count >= 16 && trimmed.allSatisfy({ $0.isHexDigit || $0 == "-" }) && profile.digitCount >= 3 {
                    return true
                }
                if trimmed.count >= 6 && hasInternalCamelCaseTransition(trimmed) {
                    return true
                }
            }
        }

        if !hasNonLatinScript && trimmed.count >= 10 {
            let codePunct: Set<Character> = ["{", "}", ";", "=", "<", ">", "(", ")", "[", "]"]
            let punctHits = trimmed.filter { codePunct.contains($0) }.count
            let hasStatementMarker = trimmed.hasSuffix(";")
                || trimmed.contains("{")
                || trimmed.contains("}")
                || trimmed.contains(" = ")
                || trimmed.contains("=>")
                || trimmed.contains("==")
                || trimmed.contains("!=")
            if punctHits >= 2 && (Double(punctHits) / Double(trimmed.count) >= 0.08 || hasStatementMarker) {
                let codeKeywords = [
                    "func ", "let ", "var ", "const ", "return ", "import ",
                    "struct ", "class ", "if (", "for (", "await ", "=>", "==", "!="
                ]
                if codeKeywords.contains(where: { lower.contains($0) }) {
                    return true
                }
            }
        }

        return false
    }

    private static func hasInternalCamelCaseTransition(_ word: String) -> Bool {
        let chars = Array(word)
        guard chars.count >= 6 else { return false }
        for i in 1..<chars.count {
            if chars[i - 1].isLowercase && chars[i].isUppercase {
                return true
            }
        }
        return false
    }

    private static func isDefinitelyTargetScript(profile: ScriptProfile, targetCode: String) -> Bool {
        guard profile.letterCount > 0 else { return false }
        switch targetCode {
        case "ja":
            let jaRatio = Double(profile.kanaCount + profile.hanCount) / Double(profile.letterCount)
            return profile.kanaCount >= 1 && jaRatio >= 0.55
        case "ko":
            return Double(profile.hangulCount) / Double(profile.letterCount) >= 0.60
        case "ru":
            return Double(profile.cyrillicCount) / Double(profile.letterCount) >= 0.60
        default:
            return false
        }
    }

    private static func containsExpectedSourceScript(profile: ScriptProfile, sourceCode: String) -> Bool {
        guard profile.letterCount > 0 else { return false }
        switch sourceCode {
        case "ja":
            return (profile.kanaCount + profile.hanCount) > 0
                && Double(profile.kanaCount + profile.hanCount) / Double(profile.letterCount) >= 0.20
        case "zh-Hans", "zh-Hant":
            return profile.hanCount > 0
                && Double(profile.hanCount) / Double(profile.letterCount) >= 0.20
        case "ko":
            return profile.hangulCount > 0
                && Double(profile.hangulCount) / Double(profile.letterCount) >= 0.20
        case "ru":
            return profile.cyrillicCount > 0
                && Double(profile.cyrillicCount) / Double(profile.letterCount) >= 0.20
        case "en", "fr", "de", "es":
            return profile.latinCount > 0
                && Double(profile.latinCount) / Double(profile.letterCount) >= 0.35
        default:
            return true
        }
    }

    /// NLTokenizerを使わずゼロオーバーヘッドの単一スカラー走査で語彙トークンを抽出
    private static func extractLexicalWordTokens(_ text: String) -> [String] {
        var tokens: [String] = []
        var cur = ""
        for sc in text.unicodeScalars {
            if sc.properties.isAlphabetic || sc.properties.isIdeographic {
                cur.unicodeScalars.append(sc)
            } else if sc == "'" || sc == "’" {
                if !cur.isEmpty {
                    cur.unicodeScalars.append(sc)
                }
            } else if !cur.isEmpty {
                tokens.append(cur)
                cur = ""
            }
        }
        if !cur.isEmpty {
            tokens.append(cur)
        }
        return tokens
    }

    private static func isLatinOCRGibberish(tokens: [String], totalLetters: Int) -> Bool {
        guard !tokens.isEmpty else { return true }

        if tokens.count >= 3 && tokens.allSatisfy({ $0.count <= 1 }) {
            return true
        }

        if totalLetters >= 4 {
            let vowels = CharacterSet(charactersIn: "aeiouyAEIOUYàâäáãåèéêëìíîïòóôöõøùúûüÿæœÀÂÄÁÃÅÈÉÊËÌÍÎÏÒÓÔÖÕØÙÚÛÜŸÆŒ")
            let wordsWithVowel = tokens.filter { tok in
                tok.unicodeScalars.contains(where: { vowels.contains($0) })
            }.count
            if wordsWithVowel == 0 {
                return true
            }
        }

        return false
    }

    private static func mapNLLanguage(_ lang: NLLanguage) -> String? {
        switch lang {
        case .english: return "en"
        case .japanese: return "ja"
        case .simplifiedChinese: return "zh-Hans"
        case .traditionalChinese: return "zh-Hant"
        case .korean: return "ko"
        case .french: return "fr"
        case .german: return "de"
        case .spanish: return "es"
        case .russian: return "ru"
        default: return nil
        }
    }

    private static func isPredominantlyLanguage(
        _ langCode: String,
        profile: ScriptProfile,
        hypotheses: [NLLanguage: Double]
    ) -> Bool {
        if isDefinitelyTargetScript(profile: profile, targetCode: langCode) {
            return true
        }
        guard profile.letterCount >= 6 else { return false }
        for (nlLang, prob) in hypotheses {
            if mapNLLanguage(nlLang) == langCode && prob >= 0.78 {
                return true
            }
        }
        return false
    }

    private static func hypothesisProbability(
        for code: String,
        in hypotheses: [NLLanguage: Double],
        profile: ScriptProfile
    ) -> Double {
        for (nlLang, prob) in hypotheses where mapNLLanguage(nlLang) == code {
            return prob
        }
        return containsExpectedSourceScript(profile: profile, sourceCode: code) ? 0.65 : 0.0
    }

    private static func resolveAutoLanguage(
        profile: ScriptProfile,
        hypotheses: [NLLanguage: Double],
        targetCode: String
    ) -> (code: String, confidence: Double)? {
        guard profile.letterCount > 0 else { return nil }

        if profile.kanaCount > 0 && Double(profile.kanaCount + profile.hanCount) / Double(profile.letterCount) >= 0.35 {
            return ("ja", max(0.85, hypothesisProbability(for: "ja", in: hypotheses, profile: profile)))
        }
        if profile.hangulCount > 0 && Double(profile.hangulCount) / Double(profile.letterCount) >= 0.35 {
            return ("ko", max(0.85, hypothesisProbability(for: "ko", in: hypotheses, profile: profile)))
        }
        if profile.cyrillicCount > 0 && Double(profile.cyrillicCount) / Double(profile.letterCount) >= 0.35 {
            return ("ru", max(0.80, hypothesisProbability(for: "ru", in: hypotheses, profile: profile)))
        }
        if profile.hanCount > 0 && profile.kanaCount == 0 && Double(profile.hanCount) / Double(profile.letterCount) >= 0.35 {
            let hantProb = hypotheses[.traditionalChinese] ?? 0
            let hansProb = hypotheses[.simplifiedChinese] ?? 0
            let code = (hantProb > hansProb + 0.05) ? "zh-Hant" : "zh-Hans"
            return (code, max(0.75, max(hantProb, hansProb)))
        }

        let sortedSupported = hypotheses
            .compactMap { (nl, prob) -> (code: String, prob: Double)? in
                guard let code = mapNLLanguage(nl) else { return nil }
                return (code, prob)
            }
            .sorted { $0.prob > $1.prob }

        let totalSupportedProb = sortedSupported.reduce(0.0) { $0 + $1.prob }

        if let best = sortedSupported.first {
            if profile.letterCount >= 8 && best.prob < 0.42 && totalSupportedProb < 0.58 {
                return nil
            }
            if best.prob >= 0.35 || (profile.letterCount < 8 && totalSupportedProb >= 0.45) {
                return (best.code, best.prob)
            }
        }

        let allNaturalProb = hypotheses
            .filter { $0.key != .undetermined }
            .reduce(0.0) { $0 + $1.value }
        if profile.latinCount >= 3 && profile.letterCount <= 14 && allNaturalProb >= 0.55 {
            let fallback = (targetCode == "en") ? "fr" : "en"
            return (fallback, allNaturalProb)
        }

        return nil
    }
}
