using System.Numerics;
using System.Text;

namespace ScreenTranslate.Core;

/// <summary>
/// 32x16 (512点) 輝度グリッド + 128bit dHash による超高速フレーム差分検出。
/// 32BGRA ピクセルバッファをポインタ直読し、静止フレームを数マイクロ秒未満でスキップしつつ、
/// 部分的なテキスト変化やスクロールを即座に検知する。
/// </summary>
public static class FrameHash
{
    public readonly struct Digest : IEquatable<Digest>
    {
        public ulong Lo { get; }
        public ulong Hi { get; }
        public byte MeanLuma { get; }
        public byte[] GridLuma { get; }

        public Digest(ulong lo, ulong hi, byte meanLuma, byte[]? gridLuma = null)
        {
            Lo = lo;
            Hi = hi;
            MeanLuma = meanLuma;
            GridLuma = gridLuma ?? Array.Empty<byte>();
        }

        public bool Equals(Digest other)
        {
            if (Lo != other.Lo || Hi != other.Hi || MeanLuma != other.MeanLuma)
                return false;
            return GridLuma.AsSpan().SequenceEqual(other.GridLuma);
        }

        public override bool Equals(object? obj) => obj is Digest d && Equals(d);
        public override int GetHashCode() => HashCode.Combine(Lo, Hi, MeanLuma, GridLuma.Length);
    }

    public static unsafe Digest? ComputeFromBgra32(IntPtr baseAddress, int width, int height, int stride)
    {
        if (baseAddress == IntPtr.Zero || width < 17 || height < 8 || Math.Abs(stride) < width * 4)
            return null;

        const int gridCols = 32;
        const int gridRows = 16;
        var grid = new byte[gridCols * gridRows];

        byte* ptr = (byte*)baseAddress;
        ulong lo = 0;
        ulong hi = 0;
        int lumaSum = 0;
        int idx = 0;

        for (int r = 0; r < gridRows; r++)
        {
            int y = (r * (height - 1)) / (gridRows - 1);
            byte* rowPtr = ptr + (long)y * stride;
            for (int c = 0; c < gridCols; c++)
            {
                int x = (c * (width - 1)) / (gridCols - 1);
                int luma = SampleBgraLuma(rowPtr, x);
                byte clamped = (byte)Math.Clamp(luma, 0, 255);
                grid[idx++] = clamped;
                lumaSum += clamped;
            }
        }

        int bitIndex = 0;
        for (int row = 0; row < 8; row++)
        {
            int y = (row * (height - 1)) / 7;
            byte* rowPtr = ptr + (long)y * stride;
            int prevLuma = SampleBgraLuma(rowPtr, 0);
            for (int col = 1; col <= 16; col++)
            {
                int x = (col * (width - 1)) / 16;
                int curLuma = SampleBgraLuma(rowPtr, x);
                if (curLuma > prevLuma + 5)
                {
                    if (bitIndex < 64)
                        lo |= 1UL << bitIndex;
                    else
                        hi |= 1UL << (bitIndex - 64);
                }
                prevLuma = curLuma;
                bitIndex++;
            }
        }

        byte mean = (byte)Math.Clamp(lumaSum / grid.Length, 0, 255);
        return new Digest(lo, hi, mean, grid);
    }

    public static unsafe Digest? ComputeFromBgra32(ReadOnlySpan<byte> buffer, int width, int height, int stride)
    {
        if (width < 17 || height < 8 || buffer.Length < height * stride)
            return null;
        fixed (byte* p = buffer)
        {
            return ComputeFromBgra32((IntPtr)p, width, height, stride);
        }
    }

    private static unsafe int SampleBgraLuma(byte* rowPtr, int x)
    {
        byte* px = rowPtr + (x * 4);
        return (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
    }

    public static int HammingDistance(in Digest a, in Digest b)
    {
        return BitOperations.PopCount(a.Lo ^ b.Lo) + BitOperations.PopCount(a.Hi ^ b.Hi);
    }

    /// <summary>
    /// 512点の輝度差（閾値 &gt; 10）が2点以上変化したか、または平均輝度/dHashが変化した場合に「変化あり」と判定する。
    /// </summary>
    public static bool IsEffectivelyUnchanged(in Digest a, in Digest b)
    {
        if (a.GridLuma.Length > 0 && a.GridLuma.Length == b.GridLuma.Length)
        {
            int changedPoints = 0;
            var sa = a.GridLuma;
            var sb = b.GridLuma;
            for (int i = 0; i < sa.Length; i++)
            {
                if (Math.Abs(sa[i] - sb[i]) > 10)
                {
                    changedPoints++;
                    if (changedPoints >= 2)
                        return false;
                }
            }
            return Math.Abs(a.MeanLuma - b.MeanLuma) <= 4;
        }

        int dist = HammingDistance(in a, in b);
        int lumaDiff = Math.Abs(a.MeanLuma - b.MeanLuma);
        return dist <= 5 && lumaDiff <= 4;
    }
}

/// <summary>
/// 静止中は OCR ゼロで CPU を 0% に保ち、画面が動いた・変化した場合は静止を待たず即座に OCR 判定へ通す軽量ゲート。
/// </summary>
public sealed class FrameChangeGate
{
    private FrameHash.Digest? _lastOcrDigest;

    public void Reset() => _lastOcrDigest = null;

    public bool ShouldRunOcr(in FrameHash.Digest digest)
    {
        if (_lastOcrDigest is not { } last)
        {
            _lastOcrDigest = digest;
            return true;
        }

        if (FrameHash.IsEffectivelyUnchanged(in digest, in last))
            return false;

        _lastOcrDigest = digest;
        return true;
    }
}

public enum StabilityActionKind
{
    Ignore,
    Provisional,
    Commit
}

public readonly record struct StabilityAction(StabilityActionKind Kind, string Text)
{
    public static StabilityAction Ignore() => new(StabilityActionKind.Ignore, string.Empty);
    public static StabilityAction Provisional(string text) => new(StabilityActionKind.Provisional, text);
    public static StabilityAction Commit(string text) => new(StabilityActionKind.Commit, text);
}

/// <summary>
/// UTF-16 バイグラム Dice 類似度と否定語・数値シグネチャ(IMPORTANT_UPDATE)を備えた文字安定化ゲート。
/// </summary>
public sealed class TextStabilityGate
{
    private string _lastObservedNorm = string.Empty;
    private string _lastObservedCore = string.Empty;
    private string _lastCommittedCore = string.Empty;
    private string _lastCommittedCriticalSig = string.Empty;
    private int _consecutiveMatches;

    public int RequiredStableFrames { get; }

    private static readonly HashSet<string> NegationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "none", "nothing", "neither", "nor", "without",
        "cannot", "cant", "dont", "doesnt", "didnt", "wont", "wouldnt", "isnt", "arent", "wasnt", "werent"
    };

    public TextStabilityGate(int requiredStableFrames = 2)
    {
        RequiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public void Reset()
    {
        _lastObservedNorm = string.Empty;
        _lastObservedCore = string.Empty;
        _lastCommittedCore = string.Empty;
        _lastCommittedCriticalSig = string.Empty;
        _consecutiveMatches = 0;
    }

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string folded = raw.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(folded.Length);
        bool prevSpace = true;

        foreach (char ch in folded)
        {
            if (ch == '\r')
                continue;
            if (char.IsWhiteSpace(ch) && ch != '\n')
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = ch == '\n';
            }
        }

        return sb.ToString().Trim();
    }

    public static string SemanticCore(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
            return string.Empty;

        var sb = new StringBuilder(normalized.Length);
        foreach (Rune r in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(r) || r.Value == '+' || r.Value == '-')
            {
                sb.Append(Rune.ToLowerInvariant(r).ToString());
            }
        }
        return sb.ToString();
    }

    public static string CriticalSignature(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
            return "|";

        var runes = new List<Rune>(normalized.Length);
        foreach (Rune r in normalized.EnumerateRunes())
            runes.Add(r);

        var digits = new StringBuilder();
        var currentAlpha = new StringBuilder();
        var negations = new List<string>();

        for (int idx = 0; idx < runes.Count; idx++)
        {
            Rune sc = runes[idx];
            bool isNumeric = Rune.IsDigit(sc) || Rune.GetUnicodeCategory(sc) == System.Globalization.UnicodeCategory.OtherNumber;

            if (isNumeric)
            {
                bool prevIsAlpha = idx > 0 && Rune.IsLetter(runes[idx - 1]);
                bool nextIsAlpha = idx + 1 < runes.Count && Rune.IsLetter(runes[idx + 1]);
                bool prevIsDigit = idx > 0 && Rune.IsDigit(runes[idx - 1]);
                bool nextIsDigit = idx + 1 < runes.Count && Rune.IsDigit(runes[idx + 1]);
                bool isInWordZeroOrOne = (sc.Value == '0' || sc.Value == '1') && prevIsAlpha && nextIsAlpha && !prevIsDigit && !nextIsDigit;
                if (!isInWordZeroOrOne)
                    digits.Append(sc.ToString());
            }

            if (Rune.IsLetter(sc) && sc.Value < 0x3000)
            {
                currentAlpha.Append(sc.ToString());
            }
            else
            {
                if (currentAlpha.Length > 0)
                {
                    string clean = currentAlpha.ToString().ToLowerInvariant();
                    if (NegationWords.Contains(clean))
                        negations.Add(clean);
                    currentAlpha.Clear();
                }
                int v = sc.Value;
                if (v is 0x4E0D or 0x7121 or 0x672A or 0x975E)
                    negations.Add(sc.ToString());
            }
        }

        if (currentAlpha.Length > 0)
        {
            string clean = currentAlpha.ToString().ToLowerInvariant();
            if (NegationWords.Contains(clean))
                negations.Add(clean);
        }

        if (normalized.Contains("ない", StringComparison.Ordinal))
            negations.Add("ない");
        if (normalized.Contains("ません", StringComparison.Ordinal))
            negations.Add("ません");

        return $"{digits}|{string.Join(",", negations)}";
    }

    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        int maxLen = Math.Max(a.Length, b.Length);
        int minLen = Math.Min(a.Length, b.Length);
        if ((double)minLen / maxLen < 0.72)
            return ((double)minLen / maxLen) * 0.5;
        if (a.Length == 1 || b.Length == 1)
            return a == b ? 1.0 : 0.0;

        var ba = new uint[a.Length - 1];
        for (int i = 0; i < a.Length - 1; i++)
            ba[i] = ((uint)a[i] << 16) | a[i + 1];
        Array.Sort(ba);

        var bb = new uint[b.Length - 1];
        for (int i = 0; i < b.Length - 1; i++)
            bb[i] = ((uint)b[i] << 16) | b[i + 1];
        Array.Sort(bb);

        int ia = 0, ib = 0, matches = 0;
        while (ia < ba.Length && ib < bb.Length)
        {
            uint va = ba[ia], vb = bb[ib];
            if (va == vb) { matches++; ia++; ib++; }
            else if (va < vb) ia++;
            else ib++;
        }

        return (2.0 * matches) / (ba.Length + bb.Length);
    }

    public StabilityAction Feed(string rawText, float averageConfidence = 0.85f)
    {
        string norm = Normalize(rawText);
        if (string.IsNullOrEmpty(norm))
        {
            _lastObservedNorm = string.Empty;
            _lastObservedCore = string.Empty;
            _consecutiveMatches = 0;
            return StabilityAction.Ignore();
        }

        string core = SemanticCore(norm);
        string critSig = CriticalSignature(norm);

        if (!string.IsNullOrEmpty(_lastCommittedCore) && critSig == _lastCommittedCriticalSig)
        {
            if (core == _lastCommittedCore)
            {
                _lastObservedNorm = norm;
                _lastObservedCore = core;
                _consecutiveMatches = RequiredStableFrames;
                return StabilityAction.Ignore();
            }

            double postCommitThreshold = core.Length < 40 ? 0.88 : 0.91;
            if (core.Length >= 16 && Similarity(core, _lastCommittedCore) >= postCommitThreshold)
            {
                _lastObservedNorm = norm;
                _lastObservedCore = core;
                _consecutiveMatches = RequiredStableFrames;
                return StabilityAction.Ignore();
            }
        }

        string trimmedRaw = rawText.Trim();

        if (core.Length >= 60 && averageConfidence >= 0.72f)
        {
            _lastObservedNorm = norm;
            _lastObservedCore = core;
            _lastCommittedCore = core;
            _lastCommittedCriticalSig = critSig;
            _consecutiveMatches = RequiredStableFrames;
            return StabilityAction.Commit(trimmedRaw);
        }

        bool matchesPrev = core == _lastObservedCore
            || (core.Length >= 20 && !string.IsNullOrEmpty(_lastObservedCore) && Similarity(core, _lastObservedCore) >= 0.90);

        _lastObservedNorm = norm;
        _lastObservedCore = core;
        _consecutiveMatches = matchesPrev ? _consecutiveMatches + 1 : 1;

        if (RequiredStableFrames == 1 || _consecutiveMatches >= RequiredStableFrames)
        {
            _lastCommittedCore = core;
            _lastCommittedCriticalSig = critSig;
            return StabilityAction.Commit(trimmedRaw);
        }

        return StabilityAction.Provisional(trimmedRaw);
    }
}

/// <summary>
/// 固定容量の軽量スレッドセーフ対応可能な LRU キャッシュ。
/// </summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();

    public BoundedLruCache(int capacity = 128)
    {
        _capacity = Math.Max(8, capacity);
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(_capacity);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddLast(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _list.Remove(existing);
            _map.Remove(key);
        }
        else if (_map.Count >= _capacity && _list.First is { } oldest)
        {
            _list.RemoveFirst();
            _map.Remove(oldest.Value.Key);
        }

        var node = new LinkedListNode<(TKey, TValue)>((key, value));
        _list.AddLast(node);
        _map[key] = node;
    }

    public void Clear()
    {
        _map.Clear();
        _list.Clear();
    }
}
