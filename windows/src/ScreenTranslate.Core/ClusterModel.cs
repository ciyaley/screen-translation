namespace ScreenTranslate.Core;

public enum TranslationDisplayMode
{
    CardList,
    InPlace,
    CursorSpot
}

public static class TranslationDisplayModeExtensions
{
    public static string ToStorageId(this TranslationDisplayMode mode) => mode switch
    {
        TranslationDisplayMode.CardList => "cards",
        TranslationDisplayMode.InPlace => "inplace",
        TranslationDisplayMode.CursorSpot => "spot",
        _ => "cards"
    };

    public static TranslationDisplayMode FromStorageId(string? raw) => raw switch
    {
        "inplace" => TranslationDisplayMode.InPlace,
        "spot" => TranslationDisplayMode.CursorSpot,
        _ => TranslationDisplayMode.CardList
    };

    public static string ShortName(this TranslationDisplayMode mode) => mode switch
    {
        TranslationDisplayMode.CardList => "カード",
        TranslationDisplayMode.InPlace => "直接重畳",
        TranslationDisplayMode.CursorSpot => "近傍のみ",
        _ => "カード"
    };

    public static string MenuTitle(this TranslationDisplayMode mode) => mode switch
    {
        TranslationDisplayMode.CardList => "窓内カード表示（まとまり別＋ホバー枠連動）",
        TranslationDisplayMode.InPlace => "ゲーム画面へ直接重畳（クリック透過・カーソル退避）",
        TranslationDisplayMode.CursorSpot => "マウスカーソル近傍のまとまりのみ翻訳",
        _ => "窓内カード表示"
    };

    public static TranslationDisplayMode NextMode(this TranslationDisplayMode mode) => mode switch
    {
        TranslationDisplayMode.CardList => TranslationDisplayMode.InPlace,
        TranslationDisplayMode.InPlace => TranslationDisplayMode.CursorSpot,
        _ => TranslationDisplayMode.CardList
    };
}

/// <summary>
/// Windows デスクトップ座標系（左上原点: X右向き、Y下向き）の矩形構造体。
/// </summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MidX => X + Width * 0.5;
    public double MidY => Y + Height * 0.5;

    public bool Contains(double px, double py)
        => px >= X && px <= Right && py >= Y && py <= Bottom;

    public ScreenRect Inflate(double dx, double dy)
        => new(X - dx, Y - dy, Math.Max(0, Width + dx * 2), Math.Max(0, Height + dy * 2));
}

/// <summary>
/// OCRと空間クラスタリングを通過した1つの「文章のまとまり」（正規化座標 0.0〜1.0、上端原点 topY=0）。
/// </summary>
public sealed record ExtractedTextCluster
{
    public string Text { get; }
    public string SemanticCore { get; }
    public string CriticalSignature { get; }
    public float AverageConfidence { get; }
    public double MinX { get; }
    public double MaxX { get; }
    public double TopY { get; }
    public double BottomY { get; }

    public ExtractedTextCluster(
        string text,
        float averageConfidence,
        double minX,
        double maxX,
        double topY,
        double bottomY)
    {
        string norm = TextStabilityGate.Normalize(text);
        Text = norm;
        SemanticCore = TextStabilityGate.SemanticCore(norm);
        CriticalSignature = TextStabilityGate.CriticalSignature(norm);
        AverageConfidence = averageConfidence;
        MinX = minX;
        MaxX = maxX;
        TopY = topY;
        BottomY = bottomY;
    }

    public double DistanceToNormalizedPoint(double x, double y)
    {
        double dx = x < MinX ? MinX - x : (x > MaxX ? x - MaxX : 0.0);
        double dy = y < TopY ? TopY - y : (y > BottomY ? y - BottomY : 0.0);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Windows スクリーン座標（左上原点）とクラスタ正規化座標（左上原点 0..1）の相互変換ユーティリティ。
/// </summary>
public static class ClusterGeometry
{
    public static ScreenRect ToScreenRect(ExtractedTextCluster cluster, ScreenRect captureRect)
    {
        double x = captureRect.X + cluster.MinX * captureRect.Width;
        double y = captureRect.Y + cluster.TopY * captureRect.Height;
        double w = Math.Max(24.0, (cluster.MaxX - cluster.MinX) * captureRect.Width);
        double h = Math.Max(18.0, (cluster.BottomY - cluster.TopY) * captureRect.Height);
        return new ScreenRect(x, y, w, h);
    }

    public static (double X, double Y)? NormalizedCursorPoint(
        double cursorX,
        double cursorY,
        ScreenRect captureRect,
        double marginRatio = 0.08)
    {
        if (captureRect.Width < 16 || captureRect.Height < 16)
            return null;

        var expanded = captureRect.Inflate(captureRect.Width * marginRatio, captureRect.Height * marginRatio);
        if (!expanded.Contains(cursorX, cursorY))
            return null;

        double nx = Math.Clamp((cursorX - captureRect.X) / captureRect.Width, -0.1, 1.1);
        double ny = Math.Clamp((cursorY - captureRect.Y) / captureRect.Height, -0.1, 1.1);
        return (nx, ny);
    }

    public static bool ShouldFadeChipForCursor(
        ScreenRect chipScreenRect,
        double cursorX,
        double cursorY,
        double hoverPadding = 14.0)
    {
        return chipScreenRect.Inflate(hoverPadding, hoverPadding).Contains(cursorX, cursorY);
    }
}

public sealed class ClusterCardItem
{
    public required string Id { get; init; }
    public required int IndexNumber { get; init; }
    public required string SourceText { get; set; }
    public required ScreenRect ScreenBounds { get; set; }
    public string TranslatedText { get; set; } = string.Empty;
    public string StreamingText { get; set; } = string.Empty;
    public bool IsTranslating { get; set; }

    public string DisplayBodyText => !string.IsNullOrEmpty(StreamingText) ? StreamingText : TranslatedText;
    public string BadgeLabel => IndexNumber.ToString("D2");
}

public static class ClusterTranslationPlanner
{
    public sealed record CachedEntry
    {
        public string SourceText { get; }
        public string SemanticCore { get; }
        public string CriticalSignature { get; }
        public string TranslatedText { get; }

        public CachedEntry(string sourceText, string translatedText)
        {
            string norm = TextStabilityGate.Normalize(sourceText);
            SourceText = norm;
            SemanticCore = TextStabilityGate.SemanticCore(norm);
            CriticalSignature = TextStabilityGate.CriticalSignature(norm);
            TranslatedText = translatedText;
        }
    }

    public const int MaxDisplayClusters = 8;

    public static IReadOnlyList<ExtractedTextCluster> SelectActiveClusters(
        IReadOnlyList<ExtractedTextCluster> candidates,
        TranslationDisplayMode mode,
        (double X, double Y)? normalizedCursor)
    {
        if (candidates.Count == 0)
            return Array.Empty<ExtractedTextCluster>();

        if (mode == TranslationDisplayMode.CursorSpot)
        {
            if (normalizedCursor is { } cursor)
            {
                ExtractedTextCluster? nearest = null;
                double bestDist = double.MaxValue;

                foreach (var c in candidates)
                {
                    double d = c.DistanceToNormalizedPoint(cursor.X, cursor.Y);
                    if (nearest is null
                        || d < bestDist - 0.005
                        || (Math.Abs(d - bestDist) <= 0.005 && c.Text.Length > nearest.Text.Length))
                    {
                        nearest = c;
                        bestDist = d;
                    }
                }

                if (nearest is not null && bestDist <= 0.18)
                    return [nearest];
            }

            var primary = candidates.MaxBy(c => c.Text.Length);
            return primary is not null ? [primary] : Array.Empty<ExtractedTextCluster>();
        }

        if (candidates.Count <= MaxDisplayClusters)
            return candidates;

        var keepIndices = candidates
            .Select((c, i) => (Cluster: c, Index: i))
            .OrderByDescending(x => x.Cluster.Text.Length)
            .Take(MaxDisplayClusters)
            .Select(x => x.Index)
            .ToHashSet();

        return candidates.Where((_, idx) => keepIndices.Contains(idx)).ToArray();
    }

    public static string? FindCachedTranslation(
        ExtractedTextCluster cluster,
        IReadOnlyList<CachedEntry> history)
    {
        if (string.IsNullOrEmpty(cluster.SemanticCore))
            return null;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var entry = history[i];
            if (entry.CriticalSignature != cluster.CriticalSignature)
                continue;
            if (entry.SemanticCore == cluster.SemanticCore)
                return entry.TranslatedText;

            double threshold = cluster.SemanticCore.Length < 40 ? 0.89 : 0.91;
            if (cluster.SemanticCore.Length >= 14
                && TextStabilityGate.Similarity(cluster.SemanticCore, entry.SemanticCore) >= threshold)
            {
                return entry.TranslatedText;
            }
        }
        return null;
    }

    public static IReadOnlyList<int> PrioritizedIndices(
        IReadOnlyList<ExtractedTextCluster> clusters,
        IReadOnlyList<int> untranslatedIndices,
        (double X, double Y)? normalizedCursor)
    {
        if (untranslatedIndices.Count <= 1 || normalizedCursor is not { } cursor)
            return untranslatedIndices;

        var copy = untranslatedIndices.ToList();
        copy.Sort((ia, ib) =>
        {
            double da = clusters[ia].DistanceToNormalizedPoint(cursor.X, cursor.Y);
            double db = clusters[ib].DistanceToNormalizedPoint(cursor.X, cursor.Y);
            bool aNear = da <= 0.25;
            bool bNear = db <= 0.25;
            if (aNear != bNear)
                return aNear ? -1 : 1;
            if (aNear && bNear && Math.Abs(da - db) > 0.04)
                return da.CompareTo(db);
            return ia.CompareTo(ib);
        });
        return copy;
    }
}
