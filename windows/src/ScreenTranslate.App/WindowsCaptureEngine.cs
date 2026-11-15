using System.Runtime.InteropServices;
using System.Text;
using ScreenTranslate.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace ScreenTranslate.App;

public readonly record struct CaptureSelection(
    ScreenRect PhysicalRect,
    ScreenRect LogicalRect,
    ScreenRect MonitorLogicalBounds,
    double DpiScaleX,
    double DpiScaleY);

public abstract record CapturedTextEvent
{
    private CapturedTextEvent() { }
    public sealed record OcrStarted : CapturedTextEvent;
    public sealed record Idle : CapturedTextEvent;
    public sealed record Skipped(SkipReason Reason) : CapturedTextEvent;
    public sealed record CommitClusters(IReadOnlyList<ExtractedTextCluster> Clusters) : CapturedTextEvent;
}

/// <summary>
/// Windows ネイティブ画面キャプチャ + WinRT Windows.Media.Ocr エンジン。
/// - WDA_EXCLUDEFROMCAPTURE と連携して自アプリのウィンドウをキャプチャ映像から 100% 除外。
/// - Top-Down 32BGRA DIBSection を再利用し、静止中は 512点輝度比較のみで即座にスキップ。
/// - OcrLine 内の単語群を行セグメント単位へ結合して macOS Vision OCR と同等の粒度で空間フィルタへ渡す。
/// </summary>
internal sealed class WindowsCaptureEngine : IDisposable
{
    private const int MaxCaptureDimension = 1080;
    private const int IntervalMs = 400;

    public Action<CapturedTextEvent>? OnTextEvent { get; set; }
    public Action<string>? OnStatusError { get; set; }

    private readonly object _gateLock = new();
    private readonly FrameChangeGate _frameChangeGate = new();
    private readonly Dictionary<string, OcrEngine?> _ocrCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private string _sourceLangCode = "auto";
    private string _targetLangCode = "ja";
    private string _lastCommittedLayoutKey = string.Empty;
    private SkipReason? _lastEmittedSkipReason;
    private volatile bool _isProcessingOcr;
    private volatile bool _isTranslationBusy;

    private byte[]? _deferredFrameBytes;
    private int _deferredWidth;
    private int _deferredHeight;

    private IntPtr _memDc = IntPtr.Zero;
    private IntPtr _dibBitmap = IntPtr.Zero;
    private IntPtr _oldBitmap = IntPtr.Zero;
    private IntPtr _dibBits = IntPtr.Zero;
    private int _allocW;
    private int _allocH;
    private byte[]? _stagingBytes;

    public bool IsRunning => _loopCts is not null;

    public void UpdateLanguages(string sourceCode, string targetCode)
    {
        lock (_gateLock)
        {
            _sourceLangCode = sourceCode;
            _targetLangCode = targetCode;
            _frameChangeGate.Reset();
            _lastCommittedLayoutKey = string.Empty;
            _lastEmittedSkipReason = null;
            _deferredFrameBytes = null;
        }
    }

    public void InvalidateFrameCache()
    {
        lock (_gateLock)
        {
            _frameChangeGate.Reset();
            _lastCommittedLayoutKey = string.Empty;
        }
    }

    public void SetTranslationBusy(bool busy)
    {
        byte[]? pending = null;
        int pw = 0, ph = 0;

        lock (_gateLock)
        {
            _isTranslationBusy = busy;
            if (!busy && !_isProcessingOcr && _deferredFrameBytes is not null)
            {
                pending = _deferredFrameBytes;
                pw = _deferredWidth;
                ph = _deferredHeight;
                _deferredFrameBytes = null;
            }
        }

        if (pending is not null)
        {
            _ = Task.Run(() => ProcessCapturedBytesAsync(pending, pw, ph));
        }
    }

    public void Start(CaptureSelection selection, string sourceLangCode, string targetLangCode)
    {
        Stop();

        lock (_gateLock)
        {
            _sourceLangCode = sourceLangCode;
            _targetLangCode = targetLangCode;
            _frameChangeGate.Reset();
            _lastCommittedLayoutKey = string.Empty;
            _lastEmittedSkipReason = null;
            _isTranslationBusy = false;
            _deferredFrameBytes = null;
        }

        var cts = new CancellationTokenSource();
        _loopCts = cts;
        _loopTask = Task.Run(() => CaptureLoopAsync(selection, cts.Token));
    }

    public void Stop()
    {
        var cts = _loopCts;
        _loopCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            try { _loopTask?.Wait(500); } catch { }
            cts.Dispose();
        }
        _loopTask = null;
        ReleaseGdiBuffer();
    }

    private async Task CaptureLoopAsync(CaptureSelection selection, CancellationToken ct)
    {
        int srcX = (int)Math.Round(selection.PhysicalRect.X);
        int srcY = (int)Math.Round(selection.PhysicalRect.Y);
        int srcW = Math.Max(24, (int)Math.Round(selection.PhysicalRect.Width));
        int srcH = Math.Max(16, (int)Math.Round(selection.PhysicalRect.Height));

        double scale = Math.Min(1.0, (double)MaxCaptureDimension / Math.Max(srcW, srcH));
        int dstW = Math.Max(24, ((int)Math.Round(srcW * scale)) & ~1);
        int dstH = Math.Max(16, ((int)Math.Round(srcH * scale)) & ~1);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(IntervalMs));
        do
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await CaptureSingleTickAsync(srcX, srcY, srcW, srcH, dstW, dstH, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                OnStatusError?.Invoke($"画面キャプチャエラー: {ex.Message}");
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task CaptureSingleTickAsync(int srcX, int srcY, int srcW, int srcH, int dstW, int dstH, CancellationToken ct)
    {
        byte[]? readyFrame = null;

        lock (_gateLock)
        {
            if (!EnsureGdiBuffer(dstW, dstH))
                return;

            IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;

            bool ok;
            try
            {
                if (srcW == dstW && srcH == dstH)
                {
                    ok = Win32Interop.BitBlt(_memDc, 0, 0, dstW, dstH, screenDc, srcX, srcY, Win32Interop.SRCCOPY);
                }
                else
                {
                    _ = Win32Interop.SetStretchBltMode(_memDc, 4); // HALFTONE
                    _ = Win32Interop.SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
                    ok = Win32Interop.StretchBlt(_memDc, 0, 0, dstW, dstH, screenDc, srcX, srcY, srcW, srcH, Win32Interop.SRCCOPY);
                }
            }
            finally
            {
                _ = Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
            }

            if (!ok || _dibBits == IntPtr.Zero)
                return;

            var digestOpt = FrameHash.ComputeFromBgra32(_dibBits, dstW, dstH, dstW * 4);
            if (digestOpt is not { } digest || !_frameChangeGate.ShouldRunOcr(in digest))
                return;

            int byteLen = dstW * dstH * 4;
            if (_isTranslationBusy || _isProcessingOcr)
            {
                var copy = new byte[byteLen];
                Marshal.Copy(_dibBits, copy, 0, byteLen);
                _deferredFrameBytes = copy;
                _deferredWidth = dstW;
                _deferredHeight = dstH;
                return;
            }

            _stagingBytes ??= new byte[byteLen];
            if (_stagingBytes.Length != byteLen)
                _stagingBytes = new byte[byteLen];
            Marshal.Copy(_dibBits, _stagingBytes, 0, byteLen);
            readyFrame = _stagingBytes;
            _isProcessingOcr = true;
        }

        if (readyFrame is not null)
        {
            try
            {
                await ProcessCapturedBytesCoreAsync(readyFrame, dstW, dstH, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gateLock)
                {
                    _isProcessingOcr = false;
                }
            }
        }
    }

    private async Task ProcessCapturedBytesAsync(byte[] bgraBytes, int width, int height)
    {
        lock (_gateLock)
        {
            if (_isProcessingOcr) return;
            _isProcessingOcr = true;
        }

        try
        {
            await ProcessCapturedBytesCoreAsync(bgraBytes, width, height, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gateLock)
            {
                _isProcessingOcr = false;
            }
        }
    }

    private async Task ProcessCapturedBytesCoreAsync(byte[] bgraBytes, int width, int height, CancellationToken ct)
    {
        string srcLang, tgtLang;
        lock (_gateLock)
        {
            srcLang = _sourceLangCode;
            tgtLang = _targetLangCode;
        }

        OnTextEvent?.Invoke(new CapturedTextEvent.OcrStarted());

        var (rawObservations, packMissingError) = await PerformWindowsOcrAsync(bgraBytes, width, height, srcLang, tgtLang, ct).ConfigureAwait(false);
        if (packMissingError is not null)
        {
            OnStatusError?.Invoke(packMissingError);
            return;
        }

        var spatial = OcrSpatialFilter.ExtractPrimaryText(rawObservations, srcLang, tgtLang);

        if (spatial.Clusters.Count == 0)
        {
            lock (_gateLock) { _lastCommittedLayoutKey = string.Empty; }
            if (spatial.LastRejectedReason is { } rej && rej != SkipReason.Empty)
            {
                EmitSkipIfChanged(rej);
            }
            else
            {
                lock (_gateLock) { _lastEmittedSkipReason = null; }
                OnTextEvent?.Invoke(new CapturedTextEvent.Idle());
            }
            return;
        }

        var validClusters = new List<ExtractedTextCluster>(spatial.Clusters.Count);
        SkipReason lastSkip = SkipReason.LowLanguageConfidence;

        foreach (var cl in spatial.Clusters)
        {
            switch (TranslatabilityGate.Evaluate(cl.Text, srcLang, tgtLang))
            {
                case GateDecision.Skip s:
                    lastSkip = s.Reason;
                    break;
                case GateDecision.Translate tr:
                    validClusters.Add(new ExtractedTextCluster(
                        tr.CleanedText,
                        cl.AverageConfidence,
                        cl.MinX, cl.MaxX, cl.TopY, cl.BottomY));
                    break;
            }
        }

        if (validClusters.Count == 0)
        {
            lock (_gateLock) { _lastCommittedLayoutKey = string.Empty; }
            EmitSkipIfChanged(lastSkip);
            return;
        }

        string layoutKey = string.Join("||", validClusters.Select(cl =>
        {
            int qx = (int)Math.Round(cl.MinX * 50.0);
            int qy = (int)Math.Round(cl.TopY * 50.0);
            return $"{cl.SemanticCore}@{qx},{qy}";
        }));

        lock (_gateLock)
        {
            if (layoutKey == _lastCommittedLayoutKey)
            {
                _lastEmittedSkipReason = null;
                OnTextEvent?.Invoke(new CapturedTextEvent.Idle());
                return;
            }
            _lastCommittedLayoutKey = layoutKey;
            _lastEmittedSkipReason = null;
        }

        OnTextEvent?.Invoke(new CapturedTextEvent.CommitClusters(validClusters));
    }

    private void EmitSkipIfChanged(SkipReason reason)
    {
        lock (_gateLock)
        {
            if (_lastEmittedSkipReason == reason) return;
            _lastEmittedSkipReason = reason;
        }
        OnTextEvent?.Invoke(new CapturedTextEvent.Skipped(reason));
    }

    private async Task<(IReadOnlyList<OcrTextObservation> Observations, string? Error)> PerformWindowsOcrAsync(
        byte[] bgraBytes,
        int width,
        int height,
        string sourceLangCode,
        string targetLangCode,
        CancellationToken ct)
    {
        var (engine, missingError) = ResolveOcrEngine(sourceLangCode, targetLangCode);
        if (engine is null)
        {
            return (Array.Empty<OcrTextObservation>(), missingError ?? "Windows OCR 言語パックが見つかりません（Windows 設定 → 時刻と言語 → 言語と地域 から追加してください）");
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var ibuf = CryptographicBuffer.CreateFromByteArray(bgraBytes);
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                ibuf,
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Ignore);

            var ocrResult = await engine.RecognizeAsync(softwareBitmap);
            ct.ThrowIfCancellationRequested();
            if (ocrResult?.Lines is null || ocrResult.Lines.Count == 0)
                return (Array.Empty<OcrTextObservation>(), null);

            var list = new List<OcrTextObservation>();
            double invW = 1.0 / Math.Max(1, width);
            double invH = 1.0 / Math.Max(1, height);

            foreach (var line in ocrResult.Lines)
            {
                if (line.Words is { Count: > 0 })
                {
                    // macOS Vision OCR と同じ「行セグメント」粒度にまとめる（広い横空隙がある場合のみ別セグメントへ分割）
                    var segText = new StringBuilder();
                    double segMinX = 0, segMinY = 0, segMaxX = 0, segMaxY = 0;
                    bool hasSeg = false;
                    string prevWordText = string.Empty;
                    double prevRightPx = 0;

                    foreach (var word in line.Words)
                    {
                        string wt = word.Text?.Trim() ?? string.Empty;
                        if (wt.Length == 0) continue;

                        var r = word.BoundingRect;
                        double wx1 = r.X;
                        double wy1 = r.Y;
                        double wx2 = r.X + Math.Max(1.0, r.Width);
                        double wy2 = r.Y + Math.Max(1.0, r.Height);

                        if (!hasSeg)
                        {
                            segText.Append(wt);
                            segMinX = wx1; segMinY = wy1; segMaxX = wx2; segMaxY = wy2;
                            hasSeg = true;
                        }
                        else
                        {
                            double segH = Math.Max(1.0, segMaxY - segMinY);
                            double gapPx = wx1 - prevRightPx;
                            double splitThresholdPx = Math.Max(segH * 2.2, width * 0.08);

                            if (gapPx > splitThresholdPx)
                            {
                                EmitLineSegment(list, segText.ToString(), segMinX, segMinY, segMaxX, segMaxY, invW, invH);
                                segText.Clear();
                                segText.Append(wt);
                                segMinX = wx1; segMinY = wy1; segMaxX = wx2; segMaxY = wy2;
                            }
                            else
                            {
                                if (EndsWithCjkChar(prevWordText) && StartsWithCjkChar(wt) && gapPx < segH * 0.9)
                                    segText.Append(wt);
                                else
                                    segText.Append(' ').Append(wt);

                                segMinX = Math.Min(segMinX, wx1);
                                segMinY = Math.Min(segMinY, wy1);
                                segMaxX = Math.Max(segMaxX, wx2);
                                segMaxY = Math.Max(segMaxY, wy2);
                            }
                        }

                        prevWordText = wt;
                        prevRightPx = wx2;
                    }

                    if (hasSeg)
                    {
                        EmitLineSegment(list, segText.ToString(), segMinX, segMinY, segMaxX, segMaxY, invW, invH);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line.Text))
                {
                    list.Add(new OcrTextObservation(line.Text.Trim(), 0.88f, 0.05, 0.1, 0.9, 0.08));
                }
            }

            return (list, null);
        }
        catch
        {
            return (Array.Empty<OcrTextObservation>(), null);
        }
    }

    private static void EmitLineSegment(
        List<OcrTextObservation> list,
        string text,
        double x1,
        double y1,
        double x2,
        double y2,
        double invW,
        double invH)
    {
        string t = text.Trim();
        if (t.Length == 0) return;
        list.Add(new OcrTextObservation(
            t,
            0.91f,
            x1 * invW,
            y1 * invH,
            Math.Max(1.0, x2 - x1) * invW,
            Math.Max(1.0, y2 - y1) * invH));
    }

    private static bool EndsWithCjkChar(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        Rune last = default;
        foreach (Rune r in s.EnumerateRunes()) last = r;
        return TranslatabilityGate.IsCjkRune(last);
    }

    private static bool StartsWithCjkChar(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
            return TranslatabilityGate.IsCjkRune(r);
        return false;
    }

    private (OcrEngine? Engine, string? MissingError) ResolveOcrEngine(string langCode, string targetLangCode)
    {
        string cacheKey = $"{langCode}->{targetLangCode}";
        lock (_ocrCache)
        {
            if (_ocrCache.TryGetValue(cacheKey, out var cached) && cached is not null)
                return (cached, null);

            OcrEngine? created = null;
            if (langCode != "auto")
            {
                string tag = TranslationLanguage.ToWindowsBcp47Tag(langCode);
                created = TryCreateOcrForTag(tag) ?? TryCreateOcrForTag(langCode);

                // 非ラテン文字言語が明示指定されているのにOS側にOCRパックが無い場合は案内を返す
                if (created is null && langCode is "zh-Hans" or "zh-Hant" or "ko" or "ru" or "ja")
                {
                    var langObj = TranslationLanguage.SourceOptions.FirstOrDefault(x => x.Code == langCode);
                    string disp = string.IsNullOrEmpty(langObj.ShortName) ? langCode : langObj.ShortName;
                    return (null, $"Windows の「{disp}」OCR言語パックが未導入です（設定 → 時刻と言語 → 言語と地域 から追加できます）");
                }
            }

            if (created is null)
            {
                string[] preferredOrder = targetLangCode == "en"
                    ? ["ja-JP", "ja", "en-US", "en"]
                    : ["en-US", "en", "ja-JP", "ja"];

                foreach (string candidateTag in preferredOrder)
                {
                    created = TryCreateOcrForTag(candidateTag);
                    if (created is not null) break;
                }
                created ??= OcrEngine.TryCreateFromUserProfileLanguages();
            }

            if (created is not null)
                _ocrCache[cacheKey] = created;
            return (created, null);
        }
    }

    private static OcrEngine? TryCreateOcrForTag(string tag)
    {
        try
        {
            var l = new Language(tag);
            if (OcrEngine.IsLanguageSupported(l))
                return OcrEngine.TryCreateFromLanguage(l);
        }
        catch { }
        return null;
    }

    private bool EnsureGdiBuffer(int width, int height)
    {
        if (_memDc != IntPtr.Zero && _dibBitmap != IntPtr.Zero && _allocW == width && _allocH == height)
            return true;

        ReleaseGdiBuffer();

        IntPtr screenDc = Win32Interop.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            _memDc = Win32Interop.CreateCompatibleDC(screenDc);
            if (_memDc == IntPtr.Zero) return false;

            var bmi = new Win32Interop.BITMAPINFO
            {
                bmiHeader = new Win32Interop.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // Top-down DIB (左上原点)
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32Interop.BI_RGB
                }
            };

            _dibBitmap = Win32Interop.CreateDIBSection(_memDc, ref bmi, Win32Interop.DIB_RGB_COLORS, out _dibBits, IntPtr.Zero, 0);
            if (_dibBitmap == IntPtr.Zero || _dibBits == IntPtr.Zero)
            {
                ReleaseGdiBuffer();
                return false;
            }

            _oldBitmap = Win32Interop.SelectObject(_memDc, _dibBitmap);
            _allocW = width;
            _allocH = height;
            return true;
        }
        finally
        {
            _ = Win32Interop.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseGdiBuffer()
    {
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            _ = Win32Interop.SelectObject(_memDc, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }
        if (_dibBitmap != IntPtr.Zero)
        {
            _ = Win32Interop.DeleteObject(_dibBitmap);
            _dibBitmap = IntPtr.Zero;
        }
        if (_memDc != IntPtr.Zero)
        {
            _ = Win32Interop.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        _dibBits = IntPtr.Zero;
        _allocW = 0;
        _allocH = 0;
    }

    public void Dispose() => Stop();
}
