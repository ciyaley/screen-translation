using System.Globalization;
using System.Text;

namespace ScreenTranslate.Core;

/// <summary>
/// OCRエンジン（Windows.Media.Ocr 等）から渡される正規化座標付き観測値（0.0〜1.0、左上原点 NormTopY=0）。
/// </summary>
public readonly record struct OcrTextObservation(
    string Text,
    float Confidence,
    double NormX,
    double NormTopY,
    double NormWidth,
    double NormHeight);

public enum SkipReason
{
    Empty,
    LowConfidenceNoise,
    SymbolsOrPunctuationOnly,
    NumericOrStatsOnly,
    KeybindOrHudControl,
    CodeOrIdentifier,
    ScriptMismatch,
    AlreadyTargetLanguage,
    LowLanguageConfidence
}

public static class SkipReasonExtensions
{
    public static string DetailJaText(this SkipReason reason) => reason switch
    {
        SkipReason.Empty => "テキスト未検出",
        SkipReason.LowConfidenceNoise => "背景ノイズ・低信頼OCRを除外",
        SkipReason.SymbolsOrPunctuationOnly => "記号・装飾文字のみのため対象外",
        SkipReason.NumericOrStatsOnly => "数値・ステータス表記のため対象外",
        SkipReason.KeybindOrHudControl => "キー操作・UIラベルのため対象外",
        SkipReason.CodeOrIdentifier => "URL・パス・識別子のため対象外",
        SkipReason.ScriptMismatch => "選択中の翻訳元言語の文字を含まないため対象外",
        SkipReason.AlreadyTargetLanguage => "すでに翻訳先の言語のため対象外",
        SkipReason.LowLanguageConfidence => "言語として成立しない断片のため対象外",
        _ => string.Empty
    };

    public static string ShortBadgeJaText(this SkipReason reason) => reason switch
    {
        SkipReason.Empty => string.Empty,
        SkipReason.LowConfidenceNoise => "除外: ノイズ",
        SkipReason.SymbolsOrPunctuationOnly => "除外: 記号",
        SkipReason.NumericOrStatsOnly => "除外: 数値",
        SkipReason.KeybindOrHudControl => "除外: UI操作",
        SkipReason.CodeOrIdentifier => "除外: 識別子",
        SkipReason.ScriptMismatch => "除外: 別文字系",
        SkipReason.AlreadyTargetLanguage => "除外: 翻訳先言語",
        SkipReason.LowLanguageConfidence => "除外: 断片",
        _ => string.Empty
    };
}

public sealed record SpatialExtractionResult(
    string Text,
    IReadOnlyList<ExtractedTextCluster> Clusters,
    float AverageConfidence,
    int KeptClusterCount,
    int DroppedObservationCount,
    SkipReason? LastRejectedReason);

public abstract record GateDecision
{
    private GateDecision() { }
    public sealed record Translate(string CleanedText, string ResolvedSourceCode, double LanguageConfidence) : GateDecision;
    public sealed record Skip(SkipReason Reason) : GateDecision;
}

/// <summary>
/// 第1段: OCR観測値の信頼度フィルタ・横空隙10%分割・見出し横装飾除去・折り返し行結合・周辺HUDバッジ分離。
/// </summary>
public static class OcrSpatialFilter
{
    public const float StandardMinConfidence = 0.50f;
    public const float ShortTokenMinConfidence = 0.72f;
    public const double HorizontalSplitGapRatio = 0.10;

    private static readonly HashSet<string> ValidTwoLetterWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "in", "on", "at", "to", "of", "is", "it", "as", "be", "by", "or",
        "no", "so", "up", "we", "he", "my", "me", "us", "do", "go", "if", "am", "an"
    };

    private readonly record struct PositionedItem(
        string Text,
        float Confidence,
        double MinX,
        double MaxX,
        double TopY,
        double BottomY)
    {
        public double CenterY => (TopY + BottomY) * 0.5;
        public double Height => Math.Max(0.001, BottomY - TopY);
    }

    private static bool IsLikelyTrailingOrnament(in PositionedItem item, double gapFromPrev)
    {
        string trimmed = item.Text.Trim();
        if (trimmed.Length > 2)
            return false;

        foreach (Rune r in trimmed.EnumerateRunes())
        {
            if (TranslatabilityGate.IsCjkRune(r))
                return false;
        }

        if (gapFromPrev >= Math.Max(0.018, item.Height * 0.55))
        {
            if (!ValidTwoLetterWords.Contains(trimmed))
                return true;
            if (item.Confidence < 0.88f)
                return true;
        }
        return false;
    }

    private sealed class HorizontalChunk
    {
        public required List<PositionedItem> Items { get; init; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double TopY { get; set; }
        public double BottomY { get; set; }
        public double MidX => (MinX + MaxX) * 0.5;
        public double Height => Math.Max(0.001, BottomY - TopY);

        public string JoinedText()
        {
            if (Items.Count == 0) return string.Empty;
            var sb = new StringBuilder(Items[0].Text);
            for (int idx = 1; idx < Items.Count; idx++)
            {
                var prev = Items[idx - 1];
                var cur = Items[idx];
                double gap = cur.MinX - prev.MaxX;
                if (idx == Items.Count - 1 && IsLikelyTrailingOrnament(in cur, gap))
                    continue;

                if (gap < 0.025 && EndsWithCjk(prev.Text) && StartsWithCjk(cur.Text))
                    sb.Append(cur.Text);
                else
                    sb.Append(' ').Append(cur.Text);
            }
            return sb.ToString();
        }

        public float WeightedConfidence()
        {
            float totalW = 0f, sum = 0f;
            foreach (var it in Items)
            {
                float w = Math.Max(1, it.Text.Length);
                sum += it.Confidence * w;
                totalW += w;
            }
            return totalW > 0f ? sum / totalW : 0f;
        }
    }

    private sealed class TextClusterBuilder
    {
        public required List<HorizontalChunk> Lines { get; init; }
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double TopY { get; set; }
        public double BottomY { get; set; }

        public string BuildText()
        {
            if (Lines.Count == 0) return string.Empty;
            var sb = new StringBuilder(Lines[0].JoinedText());
            var prevLine = Lines[0];

            for (int i = 1; i < Lines.Count; i++)
            {
                var line = Lines[i];
                string nextText = line.JoinedText();
                string currentOut = sb.ToString();
                if (ShouldSoftJoin(currentOut, prevLine, nextText, line))
                {
                    if (EndsWithCjk(currentOut) && StartsWithCjk(nextText))
                        sb.Append(nextText);
                    else
                        sb.Append(' ').Append(nextText);
                }
                else
                {
                    sb.Append('\n').Append(nextText);
                }
                prevLine = line;
            }
            return sb.ToString();
        }

        public int CharCount => Lines.Sum(l => l.JoinedText().Length);

        public float WeightedConfidence()
        {
            float totalW = 0f, sum = 0f;
            foreach (var line in Lines)
            {
                string jt = line.JoinedText();
                float w = Math.Max(1, jt.Length);
                sum += line.WeightedConfidence() * w;
                totalW += w;
            }
            return totalW > 0f ? sum / totalW : 0f;
        }

        private static bool ShouldSoftJoin(string prevText, HorizontalChunk prevLine, string nextText, HorizontalChunk nextLine)
        {
            string tp = prevText.Trim();
            string tn = nextText.Trim();
            if (tp.Length == 0 || tn.Length == 0) return false;

            double maxH = Math.Max(prevLine.Height, nextLine.Height);
            double minH = Math.Min(prevLine.Height, nextLine.Height);
            if (maxH > 0 && (minH / maxH) < 0.72)
                return false;

            char lastCh = tp[^1];
            if (lastCh is '.' or '!' or '?' or '。' or '！' or '？' or ':' or '：' or ';' or '；' or '…' or '」' or '』' or '】' or ')')
                return false;

            char firstCh = tn[0];
            if (firstCh is '•' or '・' or '-' or '*' or '▪' or '▫' or '◆' or '◇' or '▶')
                return false;

            return true;
        }
    }

    private static bool EndsWithCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        Rune last = default;
        foreach (Rune r in s.EnumerateRunes()) last = r;
        return TranslatabilityGate.IsCjkRune(last);
    }

    private static bool StartsWithCjk(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
            return TranslatabilityGate.IsCjkRune(r);
        return false;
    }

    public static SpatialExtractionResult ExtractPrimaryText(
        IReadOnlyList<OcrTextObservation> observations,
        string sourceLangCode = "auto",
        string targetLangCode = "ja")
    {
        if (observations.Count == 0)
            return new SpatialExtractionResult(string.Empty, Array.Empty<ExtractedTextCluster>(), 0f, 0, 0, SkipReason.Empty);

        var validItems = new List<PositionedItem>(observations.Count);
        int dropped = 0;
        SkipReason? lastReject = null;

        foreach (var obs in observations)
        {
            string trimmed = obs.Text.Trim();
            if (trimmed.Length == 0)
            {
                dropped++;
                continue;
            }

            var visibleRunes = new List<Rune>(trimmed.Length);
            foreach (Rune r in trimmed.EnumerateRunes())
            {
                if (!Rune.IsWhiteSpace(r))
                    visibleRunes.Add(r);
            }

            if (visibleRunes.Count == 1)
            {
                Rune only = visibleRunes[0];
                if (!Rune.IsLetterOrDigit(only))
                {
                    dropped++;
                    lastReject = SkipReason.SymbolsOrPunctuationOnly;
                    continue;
                }
            }

            float minConf = visibleRunes.Count <= 2 ? ShortTokenMinConfidence : StandardMinConfidence;
            if (obs.Confidence < minConf)
            {
                dropped++;
                lastReject = SkipReason.LowConfidenceNoise;
                continue;
            }

            if (TranslatabilityGate.IsRepetitiveOcrArtifact(trimmed))
            {
                dropped++;
                lastReject = SkipReason.LowConfidenceNoise;
                continue;
            }

            double topY = Math.Clamp(obs.NormTopY, 0.0, 1.0);
            double bottomY = Math.Clamp(obs.NormTopY + obs.NormHeight, topY + 0.001, 1.0);
            double minX = Math.Clamp(obs.NormX, 0.0, 1.0);
            double maxX = Math.Clamp(obs.NormX + obs.NormWidth, minX + 0.001, 1.0);

            validItems.Add(new PositionedItem(trimmed, obs.Confidence, minX, maxX, topY, bottomY));
        }

        if (validItems.Count == 0)
        {
            return new SpatialExtractionResult(
                string.Empty,
                Array.Empty<ExtractedTextCluster>(),
                0f, 0, dropped,
                lastReject ?? SkipReason.LowConfidenceNoise);
        }

        validItems.Sort((a, b) =>
        {
            if (Math.Abs(a.CenterY - b.CenterY) > Math.Min(a.Height, b.Height) * 0.55)
                return a.CenterY.CompareTo(b.CenterY);
            return a.MinX.CompareTo(b.MinX);
        });

        var rowBuckets = new List<(double CenterY, double Height, List<PositionedItem> Items)>();
        foreach (var item in validItems)
        {
            int matchedIdx = -1;
            for (int i = rowBuckets.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(rowBuckets[i].CenterY - item.CenterY) <= Math.Min(rowBuckets[i].Height, item.Height) * 0.55)
                {
                    matchedIdx = i;
                    break;
                }
            }

            if (matchedIdx >= 0)
            {
                var b = rowBuckets[matchedIdx];
                b.Items.Add(item);
                double count = b.Items.Count;
                double newCy = ((b.CenterY * (count - 1.0)) + item.CenterY) / count;
                rowBuckets[matchedIdx] = (newCy, Math.Max(b.Height, item.Height), b.Items);
            }
            else
            {
                rowBuckets.Add((item.CenterY, item.Height, new List<PositionedItem> { item }));
            }
        }

        rowBuckets.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));

        var horizontalChunks = new List<HorizontalChunk>();
        foreach (var bucket in rowBuckets)
        {
            var sortedLine = bucket.Items.OrderBy(x => x.MinX).ToList();
            var currentGroup = new List<PositionedItem>();
            double? prevRight = null;

            foreach (var item in sortedLine)
            {
                if (prevRight is { } right && (item.MinX - right) > HorizontalSplitGapRatio)
                {
                    if (MakeChunk(currentGroup) is { } ch)
                        horizontalChunks.Add(ch);
                    currentGroup = [item];
                    prevRight = item.MaxX;
                }
                else
                {
                    currentGroup.Add(item);
                    prevRight = Math.Max(prevRight ?? item.MaxX, item.MaxX);
                }
            }
            if (MakeChunk(currentGroup) is { } tail)
                horizontalChunks.Add(tail);
        }

        var clusters = new List<TextClusterBuilder>();
        foreach (var chunk in horizontalChunks)
        {
            int matchIdx = -1;
            for (int i = clusters.Count - 1; i >= 0; i--)
            {
                var cl = clusters[i];
                var lastLine = cl.Lines[^1];
                double maxH = Math.Max(lastLine.Height, chunk.Height);
                double minH = Math.Min(lastLine.Height, chunk.Height);
                if (maxH > 0 && (minH / maxH) < 0.70)
                    continue;

                double vGap = chunk.TopY - lastLine.BottomY;
                if (vGap < -minH * 0.4 || vGap > maxH * 1.45)
                    continue;

                double overlap = Math.Min(cl.MaxX, chunk.MaxX) - Math.Max(cl.MinX, chunk.MinX);
                bool leftAligned = Math.Abs(cl.MinX - chunk.MinX) <= 0.12;
                bool centerAligned = Math.Abs(((cl.MinX + cl.MaxX) * 0.5) - chunk.MidX) <= 0.12;
                if (overlap > 0 || leftAligned || centerAligned)
                {
                    matchIdx = i;
                    break;
                }
            }

            if (matchIdx >= 0)
            {
                var cl = clusters[matchIdx];
                cl.Lines.Add(chunk);
                cl.MinX = Math.Min(cl.MinX, chunk.MinX);
                cl.MaxX = Math.Max(cl.MaxX, chunk.MaxX);
                cl.TopY = Math.Min(cl.TopY, chunk.TopY);
                cl.BottomY = Math.Max(cl.BottomY, chunk.BottomY);
            }
            else
            {
                clusters.Add(new TextClusterBuilder
                {
                    Lines = [chunk],
                    MinX = chunk.MinX,
                    MaxX = chunk.MaxX,
                    TopY = chunk.TopY,
                    BottomY = chunk.BottomY
                });
            }
        }

        var surviving = new List<TextClusterBuilder>();
        foreach (var cl in clusters)
        {
            string candidate = cl.BuildText();
            if (TranslatabilityGate.StructuralRejectReason(candidate, sourceLangCode, targetLangCode) is { } rej)
            {
                dropped += cl.Lines.Sum(l => l.Items.Count);
                lastReject = rej;
                continue;
            }
            surviving.Add(cl);
        }

        if (surviving.Count >= 2)
        {
            int maxChars = surviving.Max(c => c.CharCount);
            if (maxChars >= 10)
            {
                surviving = surviving.Where(cl =>
                {
                    string tc = cl.BuildText().Trim();
                    if (TranslatabilityGate.IsPeripheralHudChromeBadge(tc) && cl.CharCount * 3 < maxChars)
                    {
                        dropped++;
                        return false;
                    }
                    if (cl.CharCount <= 2 && maxChars >= 20)
                    {
                        dropped++;
                        return false;
                    }
                    return true;
                }).ToList();
            }
        }

        if (surviving.Count == 0)
        {
            return new SpatialExtractionResult(
                string.Empty,
                Array.Empty<ExtractedTextCluster>(),
                0f, 0, dropped,
                lastReject ?? SkipReason.NumericOrStatsOnly);
        }

        surviving.Sort((a, b) =>
        {
            if (Math.Abs(a.TopY - b.TopY) > 0.04)
                return a.TopY.CompareTo(b.TopY);
            return a.MinX.CompareTo(b.MinX);
        });

        var extractedClusters = new List<ExtractedTextCluster>(surviving.Count);
        var finalLines = new List<string>(surviving.Count);
        float totalW = 0f, confSum = 0f;

        foreach (var cl in surviving)
        {
            string cleanText = cl.BuildText().Trim();
            if (cleanText.Length == 0) continue;
            finalLines.Add(cleanText);
            float w = Math.Max(1, cl.CharCount);
            float wc = cl.WeightedConfidence();
            confSum += wc * w;
            totalW += w;
            extractedClusters.Add(new ExtractedTextCluster(cleanText, wc, cl.MinX, cl.MaxX, cl.TopY, cl.BottomY));
        }

        return new SpatialExtractionResult(
            string.Join("\n", finalLines),
            extractedClusters,
            totalW > 0f ? confSum / totalW : 0f,
            extractedClusters.Count,
            dropped,
            null);
    }

    private static HorizontalChunk? MakeChunk(List<PositionedItem> items)
    {
        if (items.Count == 0) return null;
        double minX = items.Min(i => i.MinX);
        double maxX = items.Max(i => i.MaxX);
        double topY = items.Min(i => i.TopY);
        double bottomY = items.Max(i => i.BottomY);
        return new HorizontalChunk
        {
            Items = new List<PositionedItem>(items),
            MinX = minX,
            MaxX = maxX,
            TopY = topY,
            BottomY = bottomY
        };
    }
}

/// <summary>
/// 第2・第3段: 決定的構造・文字体系ゲート ＋ 軽量言語特徴スコアリングゲート。
/// </summary>
public static class TranslatabilityGate
{
    private static readonly HashSet<string> StatAndUnitTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "fps", "ms", "ping", "hp", "mp", "sp", "ap", "ep", "cp", "lp", "vp",
        "exp", "xp", "lv", "lvl", "atk", "def", "matk", "mdef", "spd", "crit", "dps",
        "kb", "mb", "gb", "tb", "kbps", "mbps", "hz", "khz", "mhz", "ghz",
        "px", "pt", "cm", "mm", "km", "kg", "sec", "min", "hr", "am", "pm", "ver", "v"
    };

    private static readonly HashSet<string> StandaloneHardwareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "esc", "tab", "caps", "shift", "ctrl", "control", "alt", "opt", "option",
        "cmd", "command", "win", "fn", "enter", "return", "space", "bksp", "del", "delete",
        "ins", "home", "end", "pgup", "pgdn",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
        "l1", "l2", "l3", "r1", "r2", "r3", "lb", "rb", "lt", "rt"
    };

    private static readonly HashSet<string> PeripheralHudBadges = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "skip", "log", "menu", "pause", "paused", "hud", "ui", "rec", "live", "mute"
    };

    private static readonly HashSet<string> FrenchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "le", "la", "les", "des", "une", "est", "dans", "pour", "avec", "sur", "pas", "plus", "vous", "nous"
    };

    private static readonly HashSet<string> GermanStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "ein", "eine", "nicht", "mit", "auf", "ist", "für", "von", "dem", "den"
    };

    private static readonly HashSet<string> SpanishStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "el", "la", "los", "las", "una", "con", "por", "para", "del", "que", "como", "pero", "más", "esta"
    };

    private struct ScriptProfile
    {
        public int TotalVisible;
        public int LetterCount;
        public int DigitCount;
        public int SymbolPunctCount;
        public int KanaCount;
        public int HanCount;
        public int HangulCount;
        public int CyrillicCount;
        public int LatinCount;
        public int TraditionalHintCount;
        public int SimplifiedHintCount;
    }

    private static readonly HashSet<int> TraditionalCharacteristicHan =
    [
        '體', '國', '學', '會', '發', '經', '過', '還', '這', '們', '個', '對', '開', '關', '樹', '聖', '響', '殘'
    ];

    private static readonly HashSet<int> SimplifiedCharacteristicHan =
    [
        '体', '国', '学', '会', '发', '经', '过', '还', '这', '们', '个', '对', '开', '关', '树', '圣', '响', '残', '游', '戏', '绍'
    ];

    public static GateDecision Evaluate(string rawText, string selectedSourceCode, string targetCode)
    {
        string normalized = TextStabilityGate.Normalize(rawText);
        if (string.IsNullOrEmpty(normalized))
            return new GateDecision.Skip(SkipReason.Empty);

        if (StructuralRejectReason(normalized, selectedSourceCode, targetCode) is { } structuralReason)
            return new GateDecision.Skip(structuralReason);

        var profile = AnalyzeScripts(normalized);
        var wordTokens = ExtractLexicalWordTokens(normalized);
        if (wordTokens.Count == 0)
            return new GateDecision.Skip(SkipReason.SymbolsOrPunctuationOnly);

        if (profile.KanaCount == 0 && profile.HanCount == 0 && profile.HangulCount == 0 && profile.CyrillicCount == 0)
        {
            if (IsLatinOcrGibberish(wordTokens, profile.LetterCount))
                return new GateDecision.Skip(SkipReason.LowLanguageConfidence);
        }

        if (selectedSourceCode != "auto")
        {
            if (selectedSourceCode == targetCode)
                return new GateDecision.Skip(SkipReason.AlreadyTargetLanguage);

            string guessed = GuessAutoLanguage(profile, wordTokens, targetCode);
            if (guessed == targetCode && profile.LetterCount >= 8 && IsDefinitelyTargetLanguage(profile, wordTokens, targetCode))
                return new GateDecision.Skip(SkipReason.AlreadyTargetLanguage);

            return new GateDecision.Translate(normalized, selectedSourceCode, 0.92);
        }

        string resolved = GuessAutoLanguage(profile, wordTokens, targetCode);
        if (string.IsNullOrEmpty(resolved))
            return new GateDecision.Skip(SkipReason.LowLanguageConfidence);
        if (resolved == targetCode)
            return new GateDecision.Skip(SkipReason.AlreadyTargetLanguage);

        return new GateDecision.Translate(normalized, resolved, 0.90);
    }

    public static SkipReason? StructuralRejectReason(string text, string sourceLangCode, string targetLangCode)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return SkipReason.Empty;

        if (IsRepetitiveOcrArtifact(trimmed))
            return SkipReason.LowConfidenceNoise;

        var profile = AnalyzeScripts(trimmed);
        if (profile.LetterCount == 0)
            return profile.DigitCount > 0 ? SkipReason.NumericOrStatsOnly : SkipReason.SymbolsOrPunctuationOnly;

        if (profile.TotalVisible >= 4 && (double)profile.LetterCount / profile.TotalVisible < 0.28)
            return profile.DigitCount >= profile.SymbolPunctCount ? SkipReason.NumericOrStatsOnly : SkipReason.SymbolsOrPunctuationOnly;

        if (IsKeybindPrompt(trimmed))
            return SkipReason.KeybindOrHudControl;

        if (IsNumericOrStatExpression(trimmed))
            return SkipReason.NumericOrStatsOnly;

        if (IsCodeOrIdentifier(trimmed, profile))
            return SkipReason.CodeOrIdentifier;

        if (IsDefinitelyTargetScript(profile, targetLangCode))
            return SkipReason.AlreadyTargetLanguage;

        if (sourceLangCode != "auto" && !ContainsExpectedSourceScript(profile, sourceLangCode))
            return SkipReason.ScriptMismatch;

        return null;
    }

    public static bool IsPeripheralHudChromeBadge(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        if (PeripheralHudBadges.Contains(lower))
            return true;

        var parts = lower.Split([' ', '\t', '\n', '/', '|'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 1 and <= 3 && parts.All(p => PeripheralHudBadges.Contains(p));
    }

    public static bool IsRepetitiveOcrArtifact(string text)
    {
        var runes = new List<int>(text.Length);
        foreach (Rune r in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(r))
                runes.Add(r.Value);
        }
        if (runes.Count < 3) return false;

        var unique = runes.ToHashSet();
        if (unique.Count == 1)
        {
            int only = runes[0];
            if (only is '|' or 'l' or 'I' or '1' or '_' or '=' or '-' or '.' || runes.Count >= 4)
                return true;
        }

        int strokeHits = runes.Count(v => v is '|' or 'l' or 'I' or '1' or '!' or '/' or '\\' or '[' or ']');
        return runes.Count >= 3 && (double)strokeHits / runes.Count >= 0.80;
    }

    public static bool IsCjkRune(Rune r)
    {
        int v = r.Value;
        return v is (>= 0x3040 and <= 0x30FF)
            or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF)
            or (>= 0xAC00 and <= 0xD7AF)
            or (>= 0xFF66 and <= 0xFF9F);
    }

    private static ScriptProfile AnalyzeScripts(string text)
    {
        var p = new ScriptProfile();
        foreach (Rune sc in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(sc)) continue;
            p.TotalVisible++;
            int v = sc.Value;

            if (v is (>= 0x3040 and <= 0x309F) or (>= 0x30A0 and <= 0x30FF) or (>= 0xFF66 and <= 0xFF9F))
            {
                p.LetterCount++;
                p.KanaCount++;
            }
            else if (v is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF) or (>= 0xF900 and <= 0xFAFF))
            {
                p.LetterCount++;
                p.HanCount++;
                if (TraditionalCharacteristicHan.Contains(v)) p.TraditionalHintCount++;
                if (SimplifiedCharacteristicHan.Contains(v)) p.SimplifiedHintCount++;
            }
            else if (v is (>= 0xAC00 and <= 0xD7AF) or (>= 0x1100 and <= 0x11FF) or (>= 0x3130 and <= 0x318F))
            {
                p.LetterCount++;
                p.HangulCount++;
            }
            else if (v is >= 0x0400 and <= 0x04FF)
            {
                p.LetterCount++;
                p.CyrillicCount++;
            }
            else if (Rune.IsLetter(sc))
            {
                p.LetterCount++;
                p.LatinCount++;
            }
            else if (Rune.IsDigit(sc) || Rune.GetUnicodeCategory(sc) == UnicodeCategory.OtherNumber)
            {
                p.DigitCount++;
            }
            else
            {
                p.SymbolPunctCount++;
            }
        }
        return p;
    }

    private static bool IsKeybindPrompt(string text)
    {
        string compact = text.Trim();
        string lower = compact.ToLowerInvariant();
        if (StandaloneHardwareKeys.Contains(lower))
            return true;

        if (compact.Length is >= 3 and <= 9)
        {
            char first = compact[0], last = compact[^1];
            if ((first == '[' && last == ']') || (first == '(' && last == ')')
                || (first == '<' && last == '>') || (first == '【' && last == '】'))
            {
                string inner = compact[1..^1].Trim().ToLowerInvariant();
                if (inner.Length == 1 || StandaloneHardwareKeys.Contains(inner))
                    return true;
            }
        }

        if (compact.Length <= 12 && !compact.Contains(' '))
        {
            foreach (char ch in compact)
            {
                if (ch is '⌘' or '⌥' or '⌃' or '⇧' or '⎋' or '⏎' or '⌫' or '⇥')
                    return true;
            }
        }

        if (compact.Contains('+') && compact.Length <= 24)
        {
            var parts = lower.Split('+').Select(s => s.Trim()).ToArray();
            if (parts.Length >= 2 && parts.All(p => StandaloneHardwareKeys.Contains(p) || p.Length == 1))
                return true;
        }

        return false;
    }

    private static bool IsNumericOrStatExpression(string text)
    {
        bool hasDigit = false;
        foreach (Rune sc in text.EnumerateRunes())
        {
            if (Rune.IsDigit(sc) || Rune.GetUnicodeCategory(sc) == UnicodeCategory.OtherNumber)
            {
                hasDigit = true;
                break;
            }
        }
        if (!hasDigit) return false;

        var words = ExtractLexicalWordTokens(text);
        if (words.Count == 0) return true;

        return words.All(w => StatAndUnitTokens.Contains(w) || string.Equals(w, "x", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCodeOrIdentifier(string text, ScriptProfile profile)
    {
        string trimmed = text.Trim();
        string lower = trimmed.ToLowerInvariant();
        bool hasNonLatin = (profile.KanaCount + profile.HanCount + profile.HangulCount + profile.CyrillicCount) > 0;

        string pHttp = "http" + "://";
        string pHttps = "https" + "://";
        string pWs = "ws" + "://";
        string pWss = "wss" + "://";
        string pFile = "file" + "://";
        string pTilde = "~" + "/";

        if (!trimmed.Any(char.IsWhiteSpace))
        {
            if (lower.StartsWith(pHttp, StringComparison.Ordinal)
                || lower.StartsWith(pHttps, StringComparison.Ordinal)
                || lower.StartsWith(pWs, StringComparison.Ordinal)
                || lower.StartsWith(pWss, StringComparison.Ordinal)
                || lower.StartsWith(pFile, StringComparison.Ordinal)
                || lower.StartsWith("www.", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.StartsWith('/') || trimmed.StartsWith(pTilde, StringComparison.Ordinal)
                || trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith("../", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.Length >= 4 && trimmed[1..].StartsWith(":\\", StringComparison.Ordinal))
                return true;
            if (trimmed.StartsWith("--", StringComparison.Ordinal) && trimmed.Length >= 4)
                return true;

            if (!hasNonLatin)
            {
                if (trimmed.Contains('_') || trimmed.Contains("::", StringComparison.Ordinal)
                    || (trimmed.Contains('@') && trimmed.Contains('.')))
                    return true;

                var dotParts = trimmed.Split('.');
                if (dotParts.Length >= 2 && trimmed.Length >= 5 && dotParts.All(p => p.Length > 0) && !trimmed.EndsWith('.'))
                    return true;

                if (lower.StartsWith("0x", StringComparison.Ordinal) && trimmed.Length >= 5)
                    return true;

                if (trimmed.Length >= 16 && trimmed.All(c => Uri.IsHexDigit(c) || c == '-') && profile.DigitCount >= 3)
                    return true;

                if (trimmed.Length >= 6 && HasInternalCamelCase(trimmed))
                    return true;
            }
        }

        if (!hasNonLatin && trimmed.Length >= 10)
        {
            int punctHits = trimmed.Count(c => c is '{' or '}' or ';' or '=' or '<' or '>' or '(' or ')' or '[' or ']');
            bool hasMarker = trimmed.EndsWith(';') || trimmed.Contains('{') || trimmed.Contains('}')
                || trimmed.Contains(" = ", StringComparison.Ordinal) || trimmed.Contains("=>", StringComparison.Ordinal)
                || trimmed.Contains("==", StringComparison.Ordinal) || trimmed.Contains("!=", StringComparison.Ordinal);

            if (punctHits >= 2 && ((double)punctHits / trimmed.Length >= 0.08 || hasMarker))
            {
                string[] kw = ["func ", "let ", "var ", "const ", "return ", "import ", "using ", "struct ", "class ", "if (", "for (", "await ", "=>", "==", "!="];
                if (kw.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    private static bool HasInternalCamelCase(string word)
    {
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsLower(word[i - 1]) && char.IsUpper(word[i]))
                return true;
        }
        return false;
    }

    private static bool IsDefinitelyTargetScript(ScriptProfile profile, string targetCode)
    {
        if (profile.LetterCount <= 0) return false;
        return targetCode switch
        {
            "ja" => profile.KanaCount >= 1 && (double)(profile.KanaCount + profile.HanCount) / profile.LetterCount >= 0.55,
            "ko" => (double)profile.HangulCount / profile.LetterCount >= 0.60,
            "ru" => (double)profile.CyrillicCount / profile.LetterCount >= 0.60,
            _ => false
        };
    }

    private static bool IsDefinitelyTargetLanguage(ScriptProfile profile, List<string> tokens, string targetCode)
    {
        if (IsDefinitelyTargetScript(profile, targetCode)) return true;
        if (targetCode is "en" && profile.LatinCount > 0 && profile.KanaCount == 0 && profile.HanCount == 0 && profile.HangulCount == 0 && profile.CyrillicCount == 0)
        {
            int enHits = tokens.Count(t => t.Equals("the", StringComparison.OrdinalIgnoreCase)
                || t.Equals("and", StringComparison.OrdinalIgnoreCase)
                || t.Equals("of", StringComparison.OrdinalIgnoreCase)
                || t.Equals("to", StringComparison.OrdinalIgnoreCase)
                || t.Equals("is", StringComparison.OrdinalIgnoreCase));
            return enHits >= 2;
        }
        return false;
    }

    private static bool ContainsExpectedSourceScript(ScriptProfile profile, string sourceCode)
    {
        if (profile.LetterCount <= 0) return false;
        return sourceCode switch
        {
            "ja" => (profile.KanaCount + profile.HanCount) > 0 && (double)(profile.KanaCount + profile.HanCount) / profile.LetterCount >= 0.20,
            "zh-Hans" or "zh-Hant" => profile.HanCount > 0 && (double)profile.HanCount / profile.LetterCount >= 0.20,
            "ko" => profile.HangulCount > 0 && (double)profile.HangulCount / profile.LetterCount >= 0.20,
            "ru" => profile.CyrillicCount > 0 && (double)profile.CyrillicCount / profile.LetterCount >= 0.20,
            "en" or "fr" or "de" or "es" => profile.LatinCount > 0 && (double)profile.LatinCount / profile.LetterCount >= 0.35,
            _ => true
        };
    }

    private static List<string> ExtractLexicalWordTokens(string text)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        foreach (Rune sc in text.EnumerateRunes())
        {
            if (Rune.IsLetter(sc))
            {
                cur.Append(sc.ToString());
            }
            else if ((sc.Value == '\'' || sc.Value == '’') && cur.Length > 0)
            {
                cur.Append(sc.ToString());
            }
            else if (cur.Length > 0)
            {
                tokens.Add(cur.ToString());
                cur.Clear();
            }
        }
        if (cur.Length > 0)
            tokens.Add(cur.ToString());
        return tokens;
    }

    private static bool IsLatinOcrGibberish(List<string> tokens, int totalLetters)
    {
        if (tokens.Count == 0) return true;
        if (tokens.Count >= 3 && tokens.All(t => t.Length <= 1)) return true;

        if (totalLetters >= 4)
        {
            const string vowels = "aeiouyAEIOUYàâäáãåèéêëìíîïòóôöõøùúûüÿæœÀÂÄÁÃÅÈÉÊËÌÍÎÏÒÓÔÖÕØÙÚÛÜŸÆŒ";
            bool anyVowel = tokens.Any(tok => tok.Any(ch => vowels.Contains(ch)));
            if (!anyVowel)
                return true;
        }
        return false;
    }

    private static string GuessAutoLanguage(ScriptProfile profile, List<string> tokens, string targetCode)
    {
        if (profile.LetterCount <= 0) return string.Empty;

        if (profile.KanaCount > 0 && (double)(profile.KanaCount + profile.HanCount) / profile.LetterCount >= 0.35)
            return "ja";
        if (profile.HangulCount > 0 && (double)profile.HangulCount / profile.LetterCount >= 0.35)
            return "ko";
        if (profile.CyrillicCount > 0 && (double)profile.CyrillicCount / profile.LetterCount >= 0.35)
            return "ru";
        if (profile.HanCount > 0 && profile.KanaCount == 0 && (double)profile.HanCount / profile.LetterCount >= 0.35)
            return profile.TraditionalHintCount > profile.SimplifiedHintCount ? "zh-Hant" : "zh-Hans";

        int frHits = tokens.Count(FrenchStopWords.Contains);
        int deHits = tokens.Count(GermanStopWords.Contains);
        int esHits = tokens.Count(SpanishStopWords.Contains);

        if (frHits >= 2 && frHits > deHits && frHits > esHits) return "fr";
        if (deHits >= 2 && deHits > frHits && deHits > esHits) return "de";
        if (esHits >= 2 && esHits > frHits && esHits > deHits) return "es";

        if (profile.LatinCount >= 2)
            return targetCode == "en" ? "fr" : "en";

        return string.Empty;
    }
}
