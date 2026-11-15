using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ScreenTranslate.Core;

public enum GpuEngineFlavor
{
    Vulkan,
    Cuda
}

public enum SidecarState
{
    NotInstalled,
    Downloading,
    Starting,
    Ready,
    Stopped,
    Error
}

public readonly record struct PlamoModelSpec(
    TranslationBackendKind Backend,
    string SizeKey,
    string DisplayLabel,
    string RepoId,
    string GgufFileName,
    long ApproxBytes,
    string ApproxSizeText);

public readonly record struct SidecarStatusSnapshot(
    SidecarState State,
    TranslationBackendKind Backend,
    string Message,
    double ProgressFraction = 0.0,
    long DownloadedBytes = 0,
    long TotalBytes = 0,
    double SpeedBytesPerSec = 0.0);

/// <summary>
/// PowerShell やバッチファイル・winget を一切使わず、アプリ単体（C# ネイティブ）で
/// 1) llama-server GPU ランタイム（Vulkan / CUDA）の自動ダウンロード・展開
/// 2) PLaMo 3 Translate GGUF 重みファイルの HTTP Range レジューム対応ダウンロード（進捗バー連携）
/// 3) バックグラウンド推論プロセスの起動・ヘルスチェック・終了（Win32 JobObject 連動）
/// を完結させるスタンドアロン管理クラス。
/// </summary>
public sealed class NativeSidecarManager : IDisposable
{
    private static readonly PlamoModelSpec Spec08B = new(
        TranslationBackendKind.Plamo08B,
        "0.8b",
        "PLaMo 0.8B (高速・軽量)",
        "pfnet/plamo-3-translate-0.8b-GGUF",
        "plamo-3-translate-0.8b-Q4_K_M.gguf",
        575_000_000L,
        "約570 MB");

    private static readonly PlamoModelSpec Spec2B = new(
        TranslationBackendKind.Plamo2B,
        "2b",
        "PLaMo 2B (標準・高精度)",
        "pfnet/plamo-3-translate-2b-GGUF",
        "plamo-3-translate-2b-Q4_K_M.gguf",
        1_810_000_000L,
        "約1.81 GB");

    private static readonly PlamoModelSpec Spec8B = new(
        TranslationBackendKind.Plamo8B,
        "8b",
        "PLaMo 8B (最高精度)",
        "pfnet/plamo-3-translate-8b-GGUF",
        "plamo-3-translate-8b-Q4_K_M.gguf",
        5_280_000_000L,
        "約5.28 GB");

    public static IReadOnlyList<PlamoModelSpec> AllModelSpecs { get; } = [Spec08B, Spec2B, Spec8B];

    public static PlamoModelSpec? GetSpec(TranslationBackendKind backend) => backend switch
    {
        TranslationBackendKind.Plamo08B => Spec08B,
        TranslationBackendKind.Plamo2B => Spec2B,
        TranslationBackendKind.Plamo8B => Spec8B,
        _ => null
    };

    private readonly string _appDataDir;
    private readonly int _port;
    private readonly Action<Process>? _onProcessSpawned;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Process? _serverProcess;
    private TranslationBackendKind? _runningBackend;
    private CancellationTokenSource? _activeOpCts;
    private SidecarStatusSnapshot _latestStatus = new(SidecarState.Stopped, TranslationBackendKind.Plamo2B, string.Empty);

    public event Action<SidecarStatusSnapshot>? StatusChanged;

    public string AppDataDir => _appDataDir;
    public string EngineRootDir => Path.Combine(_appDataDir, "engine");
    public string ModelsDir => Path.Combine(_appDataDir, "models");
    public string LogsDir => Path.Combine(_appDataDir, "logs");
    public int Port => _port;
    public SidecarStatusSnapshot LatestStatus => _latestStatus;

    public NativeSidecarManager(
        string appDataDir,
        int port = TranslationCoordinator.DefaultPlamoPort,
        Action<Process>? onProcessSpawned = null,
        HttpClient? httpClient = null)
    {
        _appDataDir = appDataDir;
        _port = port;
        _onProcessSpawned = onProcessSpawned;
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(EngineRootDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(LogsDir);

        _http = httpClient ?? CreateDownloadHttpClient();
    }

    private static HttpClient CreateDownloadHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 12,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScreenTranslate", "1.0"));
        return client;
    }

    public string GetModelFilePath(PlamoModelSpec spec)
        => Path.Combine(ModelsDir, spec.GgufFileName);

    public bool IsModelDownloaded(TranslationBackendKind backend)
    {
        if (GetSpec(backend) is not { } spec) return true;
        string path = GetModelFilePath(spec);
        if (!File.Exists(path)) return false;
        var fi = new FileInfo(path);
        return fi.Length > 100 * 1024 * 1024; // >100MB valid GGUF
    }

    public long GetPartialDownloadBytes(TranslationBackendKind backend)
    {
        if (GetSpec(backend) is not { } spec) return 0;
        string partPath = GetModelFilePath(spec) + ".part";
        return File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
    }

    public string? FindInstalledLlamaServer(GpuEngineFlavor preferredFlavor = GpuEngineFlavor.Vulkan)
    {
        string? envBin = Environment.GetEnvironmentVariable("LLAMA_SERVER_BIN");
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin))
            return envBin;

        string flavorDir = Path.Combine(EngineRootDir, preferredFlavor == GpuEngineFlavor.Cuda ? "cuda" : "vulkan");
        if (Directory.Exists(flavorDir))
        {
            string? hit = Directory.EnumerateFiles(flavorDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (hit is not null) return hit;
        }

        if (Directory.Exists(EngineRootDir))
        {
            string? anyHit = Directory.EnumerateFiles(EngineRootDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (anyHit is not null) return anyHit;
        }

        // システムに既存の llama-server.exe があればそれも利用可能
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string cand = Path.Combine(dir.Trim(), "llama-server.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
        }

        return null;
    }

    public bool NeedsDownload(TranslationBackendKind backend, GpuEngineFlavor flavor = GpuEngineFlavor.Vulkan)
    {
        if (!backend.IsPlamo()) return false;
        return FindInstalledLlamaServer(flavor) is null || !IsModelDownloaded(backend);
    }

    /// <summary>
    /// 指定モデルが既にダウンロード済みであれば即座にバックグラウンド起動し、
    /// 未ダウンロードであれば自動通信を行わず NotInstalled 状態を通知してユーザーの1クリック開始を待つ。
    /// </summary>
    public void TryAutoStartIfInstalled(TranslationBackendKind backend, GpuEngineFlavor flavor = GpuEngineFlavor.Vulkan)
    {
        if (!backend.IsPlamo())
        {
            PublishStatus(new SidecarStatusSnapshot(SidecarState.Ready, backend, "外部ローカルAPI 使用中"));
            return;
        }

        if (NeedsDownload(backend, flavor))
        {
            var spec = GetSpec(backend)!.Value;
            bool engineMissing = FindInstalledLlamaServer(flavor) is null;
            string msg = engineMissing
                ? $"初回セットアップが必要です（GPU推論エンジン + {spec.DisplayLabel} {spec.ApproxSizeText}）"
                : $"{spec.DisplayLabel} モデル（{spec.ApproxSizeText}）のダウンロードが必要です";
            PublishStatus(new SidecarStatusSnapshot(SidecarState.NotInstalled, backend, msg));
            return;
        }

        _ = DownloadAndStartAsync(backend, flavor);
    }

    public void CancelActiveOperation()
    {
        var cts = _activeOpCts;
        _activeOpCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }
    }

    /// <summary>
    /// 必要な推論エンジンおよびGGUFモデルをアプリ内でダウンロード（レジューム対応）し、そのまま起動する。
    /// </summary>
    public async Task<bool> DownloadAndStartAsync(
        TranslationBackendKind backend,
        GpuEngineFlavor flavor = GpuEngineFlavor.Vulkan,
        bool forceReinstallEngine = false)
    {
        if (GetSpec(backend) is not { } spec)
            return true;

        CancelActiveOperation();
        var opCts = new CancellationTokenSource();
        _activeOpCts = opCts;
        var ct = opCts.Token;

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceReinstallEngine && _runningBackend == backend && IsServerAliveAndListening())
            {
                PublishStatus(new SidecarStatusSnapshot(SidecarState.Ready, backend, $"{backend.ShortName()} 稼働中", 1.0));
                return true;
            }

            StopServerLocked();

            string? llamaExe = forceReinstallEngine ? null : FindInstalledLlamaServer(flavor);
            if (llamaExe is null)
            {
                llamaExe = await DownloadLlamaEngineAsync(backend, flavor, ct).ConfigureAwait(false);
            }

            string modelPath = GetModelFilePath(spec);
            if (!IsModelDownloaded(backend))
            {
                await DownloadModelGgufAsync(spec, modelPath, ct).ConfigureAwait(false);
            }

            return await StartLlamaServerLockedAsync(llamaExe, spec, modelPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bool stillMissing = NeedsDownload(backend, flavor);
            PublishStatus(new SidecarStatusSnapshot(
                stillMissing ? SidecarState.NotInstalled : SidecarState.Stopped,
                backend,
                "ダウンロードを一時停止しました（再開ボタンで続きから再開できます）"));
            return false;
        }
        catch (Exception ex)
        {
            PublishStatus(new SidecarStatusSnapshot(SidecarState.Error, backend, $"セットアップ失敗: {ex.Message}"));
            return false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<string> DownloadLlamaEngineAsync(
        TranslationBackendKind backend,
        GpuEngineFlavor flavor,
        CancellationToken ct)
    {
        PublishStatus(new SidecarStatusSnapshot(
            SidecarState.Downloading,
            backend,
            "GPU推論エンジン (llama.cpp) の最新リリース情報を確認中…",
            0.02));

        var releaseApiUri = new UriBuilder(Uri.UriSchemeHttps, "api.github.com", 443, "/repos/ggml-org/llama.cpp/releases/latest").Uri;
        using var resp = await _http.GetAsync(releaseApiUri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var assets = new List<(string Name, string Url, long Size)>();
        if (doc.RootElement.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                string url = item.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                long sz = item.TryGetProperty("size", out var s) && s.TryGetInt64(out long v) ? v : 0L;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add((name, url, sz));
            }
        }

        var selectedAssets = SelectEngineAssets(assets, flavor, out string actualSubdir);
        if (selectedAssets.Count == 0)
            throw new InvalidOperationException("Windows x64 用の llama-server 配布パッケージが見つかりませんでした。");

        string destDir = Path.Combine(EngineRootDir, actualSubdir);
        if (Directory.Exists(destDir))
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(destDir);

        for (int i = 0; i < selectedAssets.Count; i++)
        {
            var asset = selectedAssets[i];
            string zipPath = Path.Combine(EngineRootDir, asset.Name);
            string label = selectedAssets.Count > 1
                ? $"GPU推論エンジン ({i + 1}/{selectedAssets.Count}) をダウンロード中…"
                : "GPU推論エンジン (llama-server) をダウンロード中…";

            await DownloadFileWithResumeAsync(
                new Uri(asset.Url),
                zipPath,
                asset.Size,
                (dl, tot, spd) =>
                {
                    double frac = tot > 0 ? Math.Clamp((double)dl / tot, 0.0, 1.0) : 0.5;
                    PublishStatus(new SidecarStatusSnapshot(SidecarState.Downloading, backend, label, frac, dl, tot, spd));
                },
                ct).ConfigureAwait(false);

            PublishStatus(new SidecarStatusSnapshot(SidecarState.Downloading, backend, "GPU推論エンジンを展開中…", 0.98));
            ZipFile.ExtractToDirectory(zipPath, destDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }
        }

        string? exe = Directory.EnumerateFiles(destDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
        if (exe is null)
            throw new FileNotFoundException("展開後に llama-server.exe が見つかりませんでした。");

        return exe;
    }

    public static IReadOnlyList<(string Name, string Url, long Size)> SelectEngineAssets(
        IReadOnlyList<(string Name, string Url, long Size)> releaseAssets,
        GpuEngineFlavor preferredFlavor,
        out string subdirName)
    {
        if (preferredFlavor == GpuEngineFlavor.Cuda)
        {
            var cudaMain = releaseAssets.FirstOrDefault(a =>
                a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.Contains("-bin-win-cuda-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase));

            var cudaRt = releaseAssets.FirstOrDefault(a =>
                a.Name.StartsWith("cudart-llama-bin-win-cuda-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(cudaMain.Url) && !string.IsNullOrEmpty(cudaRt.Url))
            {
                subdirName = "cuda";
                return [cudaRt, cudaMain];
            }
        }

        var vulkan = releaseAssets.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("-bin-win-vulkan-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(vulkan.Url))
        {
            subdirName = "vulkan";
            return [vulkan];
        }

        var cpu = releaseAssets.FirstOrDefault(a =>
            a.Name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("-bin-win-cpu-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(cpu.Url))
        {
            subdirName = "cpu";
            return [cpu];
        }

        subdirName = "vulkan";
        return Array.Empty<(string, string, long)>();
    }

    private async Task DownloadModelGgufAsync(PlamoModelSpec spec, string destGgufPath, CancellationToken ct)
    {
        var modelUri = BuildHuggingFaceResolveUri(spec.RepoId, spec.GgufFileName);
        string label = $"{spec.DisplayLabel} をダウンロード中…";

        await DownloadFileWithResumeAsync(
            modelUri,
            destGgufPath,
            spec.ApproxBytes,
            (dl, tot, spd) =>
            {
                double frac = tot > 0 ? Math.Clamp((double)dl / tot, 0.0, 1.0) : 0.0;
                PublishStatus(new SidecarStatusSnapshot(
                    SidecarState.Downloading,
                    spec.Backend,
                    label,
                    frac,
                    dl,
                    tot,
                    spd));
            },
            ct).ConfigureAwait(false);
    }

    public static Uri BuildHuggingFaceResolveUri(string repoId, string fileName)
    {
        var ub = new UriBuilder(Uri.UriSchemeHttps, "huggingface.co", 443, $"/{repoId}/resolve/main/{fileName}")
        {
            Query = "download=true"
        };
        return ub.Uri;
    }

    private async Task DownloadFileWithResumeAsync(
        Uri uri,
        string finalFilePath,
        long fallbackExpectedBytes,
        Action<long, long, double> onProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalFilePath)!);
        string partPath = finalFilePath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existingBytes > 0)
        {
            req.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            try { File.Delete(partPath); } catch { }
            existingBytes = 0;
            using var retryReq = new HttpRequestMessage(HttpMethod.Get, uri);
            using var retryResp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            retryResp.EnsureSuccessStatusCode();
            await StreamResponseToFileAsync(retryResp, partPath, 0L, fallbackExpectedBytes, onProgress, ct).ConfigureAwait(false);
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
            long startOffset = isPartial ? existingBytes : 0L;
            await StreamResponseToFileAsync(resp, partPath, startOffset, fallbackExpectedBytes, onProgress, ct).ConfigureAwait(false);
        }

        File.Move(partPath, finalFilePath, overwrite: true);
    }

    private static async Task StreamResponseToFileAsync(
        HttpResponseMessage resp,
        string partPath,
        long startOffset,
        long fallbackExpectedBytes,
        Action<long, long, double> onProgress,
        CancellationToken ct)
    {
        long contentLen = resp.Content.Headers.ContentLength ?? 0L;
        long totalBytes = contentLen > 0 ? startOffset + contentLen : fallbackExpectedBytes;

        FileMode mode = startOffset > 0 ? FileMode.Append : FileMode.Create;
        await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.Read, bufferSize: 262_144, useAsync: true);

        byte[] buffer = new byte[262_144];
        long downloaded = startOffset;
        long speedWindowBytes = 0;
        var sw = Stopwatch.StartNew();
        long lastEmitMs = -200;
        long speedMarkMs = 0;
        double smoothedSpeed = 0.0;

        onProgress(downloaded, totalBytes, 0.0);

        while (true)
        {
            int read = await netStream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            speedWindowBytes += read;

            long nowMs = sw.ElapsedMilliseconds;
            if (nowMs - lastEmitMs >= 100)
            {
                long elapsedWin = Math.Max(1, nowMs - speedMarkMs);
                double instSpeed = speedWindowBytes * 1000.0 / elapsedWin;
                smoothedSpeed = smoothedSpeed <= 1.0 ? instSpeed : (smoothedSpeed * 0.65 + instSpeed * 0.35);
                speedWindowBytes = 0;
                speedMarkMs = nowMs;
                lastEmitMs = nowMs;
                onProgress(downloaded, Math.Max(downloaded, totalBytes), smoothedSpeed);
            }
        }

        await fileStream.FlushAsync(ct).ConfigureAwait(false);
        onProgress(downloaded, downloaded, smoothedSpeed);
    }

    private async Task<bool> StartLlamaServerLockedAsync(
        string llamaExe,
        PlamoModelSpec spec,
        string modelPath,
        CancellationToken ct)
    {
        PublishStatus(new SidecarStatusSnapshot(
            SidecarState.Starting,
            spec.Backend,
            $"{spec.Backend.ShortName()} を GPU へロード中…",
            1.0));

        string helpText = await ProbeHelpTextAsync(llamaExe).ConfigureAwait(false);
        var psi = new ProcessStartInfo
        {
            FileName = llamaExe,
            WorkingDirectory = Path.GetDirectoryName(llamaExe) ?? EngineRootDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string[] coreArgs =
        [
            "-m", modelPath,
            "--host", "127.0.0.1",
            "--port", _port.ToString(),
            "-ngl", "99",
            "-c", "8192",
            "-np", "1",
            "-b", "512",
            "-ub", "512",
            "-t", "4",
            "-tb", "4",
            "--alias", "plamo-3-translate",
            "--jinja"
        ];
        foreach (string a in coreArgs) psi.ArgumentList.Add(a);

        if (helpText.Contains("[on|off|auto]", StringComparison.OrdinalIgnoreCase)
            || helpText.Contains("on|off", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-fa");
            psi.ArgumentList.Add("on");
        }
        else if (helpText.Contains("--flash-attn", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-fa");
        }

        if (helpText.Contains("--cache-reuse", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--cache-reuse");
            psi.ArgumentList.Add("64");
        }

        if (helpText.Contains("--no-prefill-assistant", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--no-prefill-assistant");
        }

        string logFile = Path.Combine(LogsDir, "llama-server.log");
        try { File.WriteAllText(logFile, string.Empty, Encoding.UTF8); } catch { }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("llama-server.exe の起動に失敗しました。");

        _onProcessSpawned?.Invoke(proc);
        _serverProcess = proc;
        _runningBackend = spec.Backend;

        _ = DrainLogStreamAsync(proc.StandardOutput, logFile);
        _ = DrainLogStreamAsync(proc.StandardError, logFile);

        var modelsCheckUri = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", _port, "/v1/models").Uri;
        for (int i = 0; i < 240; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                string tail = ReadLogTail(logFile, 320);
                _serverProcess = null;
                _runningBackend = null;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(tail)
                    ? $"llama-server が終了しました (code {proc.ExitCode})"
                    : $"llama-server 起動失敗: {tail}");
            }

            if (await IsEndpointReadyAsync(modelsCheckUri, ct).ConfigureAwait(false))
            {
                PublishStatus(new SidecarStatusSnapshot(
                    SidecarState.Ready,
                    spec.Backend,
                    $"{spec.Backend.ShortName()} 稼働中",
                    1.0));
                return true;
            }

            await Task.Delay(350, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"{spec.Backend.ShortName()} のロードがタイムアウトしました。");
    }

    private async Task<bool> IsEndpointReadyAsync(Uri modelsUri, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMilliseconds(800));
            using var resp = await _http.GetAsync(modelsUri, linked.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool IsServerAliveAndListening()
    {
        if (_serverProcess is null || _serverProcess.HasExited)
            return false;

        try
        {
            using var tcp = new TcpClient();
            var ar = tcp.BeginConnect("127.0.0.1", _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(200)) return false;
            tcp.EndConnect(ar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ProbeHelpTextAsync(string llamaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = llamaExe,
                Arguments = "--help",
                WorkingDirectory = Path.GetDirectoryName(llamaExe) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return string.Empty;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            return outTask.Result + "\n" + errTask.Result;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task DrainLogStreamAsync(StreamReader reader, string logPath)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    lock (typeof(NativeSidecarManager))
                    {
                        File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private static string ReadLogTail(string logPath, int maxChars)
    {
        try
        {
            if (!File.Exists(logPath)) return string.Empty;
            string txt = File.ReadAllText(logPath, Encoding.UTF8).Trim();
            return txt.Length <= maxChars ? txt : txt[^maxChars..];
        }
        catch
        {
            return string.Empty;
        }
    }

    private void PublishStatus(SidecarStatusSnapshot snapshot)
    {
        _latestStatus = snapshot;
        // TranslationCoordinator とも互換連携できるよう plamo_status.json もアトミック更新
        try
        {
            string stStr = snapshot.State switch
            {
                SidecarState.Ready => "ready",
                SidecarState.Downloading => "downloading",
                SidecarState.Starting => "starting",
                SidecarState.Error or SidecarState.NotInstalled => "error",
                _ => "stopped"
            };
            string path = Path.Combine(_appDataDir, "plamo_status.json");
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(new
            {
                state = stStr,
                size = snapshot.Backend.PlamoSizeArg() ?? "",
                backend = "llama-server",
                message = snapshot.Message
            });
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }

        StatusChanged?.Invoke(snapshot);
    }

    private void StopServerLocked()
    {
        var p = _serverProcess;
        _serverProcess = null;
        _runningBackend = null;
        if (p is not null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(1500);
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }

    public void Stop()
    {
        CancelActiveOperation();
        _lifecycleLock.Wait(TimeSpan.FromSeconds(2));
        try
        {
            StopServerLocked();
        }
        finally
        {
            if (_lifecycleLock.CurrentCount == 0)
                _lifecycleLock.Release();
        }
    }

    public static string FormatProgressSummary(SidecarStatusSnapshot s)
    {
        if (s.TotalBytes <= 0) return s.Message;
        double dlMb = s.DownloadedBytes / (1024.0 * 1024.0);
        double totMb = s.TotalBytes / (1024.0 * 1024.0);
        int pct = (int)Math.Round(s.ProgressFraction * 100.0);
        string spd = s.SpeedBytesPerSec >= 1024 * 1024
            ? $" · {s.SpeedBytesPerSec / (1024.0 * 1024.0):F1} MB/s"
            : (s.SpeedBytesPerSec > 1024 ? $" · {s.SpeedBytesPerSec / 1024.0:F0} KB/s" : string.Empty);

        return totMb >= 1000.0
            ? $"{pct}%  ({dlMb / 1024.0:F2} / {totMb / 1024.0:F2} GB{spd})"
            : $"{pct}%  ({dlMb:F0} / {totMb:F0} MB{spd})";
    }

    public void Dispose() => Stop();
}
