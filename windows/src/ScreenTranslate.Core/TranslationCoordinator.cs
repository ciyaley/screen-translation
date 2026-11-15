using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScreenTranslate.Core;

/// <summary>
/// ユーザー設定の軽量JSON永続化（Windows の .runtime/settings.json に保存）。
/// </summary>
public sealed class AppUserSettings
{
    public string SourceLangCode { get; set; } = "auto";
    public string TargetLangCode { get; set; } = "ja";
    public string BackendId { get; set; } = "plamo-2b";
    public string DisplayModeId { get; set; } = "cards";
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 460.0;
    public double WindowHeight { get; set; } = 220.0;
    public string GpuFlavor { get; set; } = "vulkan";
    public string CustomOpenAiEndpoint { get; set; } = string.Empty;
    public string CustomOpenAiModel { get; set; } = string.Empty;

    public static AppUserSettings Load(string runtimeDir)
    {
        try
        {
            string path = Path.Combine(runtimeDir, "settings.json");
            if (!File.Exists(path)) return new AppUserSettings();
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppUserSettings>(json) ?? new AppUserSettings();
        }
        catch
        {
            return new AppUserSettings();
        }
    }

    public void Save(string runtimeDir)
    {
        try
        {
            Directory.CreateDirectory(runtimeDir);
            string path = Path.Combine(runtimeDir, "settings.json");
            string tmp = path + ".tmp";
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Ignore I/O contention
        }
    }
}

/// <summary>
/// PLaMo 3 Translate ローカル推論サーバー（llama-server CUDA / Vulkan）および OpenAI 互換 API クライアント。
/// PFML3プロンプト生成・文脈管理・120msスロットルSSEストリーミングを統合する。
/// </summary>
public sealed class TranslationCoordinator
{
    public const int DefaultPlamoPort = 18492;

    private readonly HttpClient _http;
    private readonly BoundedLruCache<string, string> _cache = new(128);
    private readonly List<Pfml3PromptBuilder.HistoryTurn> _historyTurns = new();
    private string? _cachedRemoteModelId;

    public TranslationCoordinator(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(65)
        };
    }

    public void ClearCache()
    {
        _cache.Clear();
        _historyTurns.Clear();
        _cachedRemoteModelId = null;
    }

    public void ResetModelCache() => _cachedRemoteModelId = null;

    public async Task<TranslationOutcome> TranslateAsync(
        string text,
        TranslationLanguage source,
        TranslationLanguage target,
        TranslationBackendKind backend,
        string? runtimeDir,
        Action<string> onStageUpdate,
        Action<string> onPartialStream,
        CancellationToken ct)
    {
        var decision = TranslatabilityGate.Evaluate(text, source.Code, target.Code);
        if (decision is GateDecision.Skip skip)
            return new TranslationOutcome.Skipped(skip.Reason);

        var tr = (GateDecision.Translate)decision;
        string cleanedText = tr.CleanedText;
        string resolvedSource = tr.ResolvedSourceCode;

        string cacheKey = $"{backend.ToStorageId()}|{resolvedSource}->{target.Code}|{cleanedText}";
        if (_cache.TryGet(cacheKey, out string? hit) && !string.IsNullOrEmpty(hit))
            return new TranslationOutcome.Translated(hit, FromCache: true);

        if (backend.IsPlamo())
        {
            return await TranslateWithPlamoAsync(
                cleanedText,
                resolvedSource,
                target.Code,
                backend,
                cacheKey,
                runtimeDir,
                onStageUpdate,
                onPartialStream,
                ct).ConfigureAwait(false);
        }

        return await TranslateWithCustomOpenAiAsync(
            cleanedText,
            resolvedSource,
            target.Code,
            cacheKey,
            runtimeDir,
            onStageUpdate,
            onPartialStream,
            ct).ConfigureAwait(false);
    }

    private static Uri BuildLoopbackUri(int port, string path)
    {
        string? envBase = Environment.GetEnvironmentVariable("SCREEN_TRANSLATE_PLAMO_BASE");
        if (!string.IsNullOrWhiteSpace(envBase) && Uri.TryCreate(envBase, UriKind.Absolute, out var customUri))
        {
            return new Uri(customUri, path);
        }
        var ub = new UriBuilder("http", "127.0.0.1", port, path);
        return ub.Uri;
    }

    private static (string State, string Message)? ReadSidecarStatus(string? runtimeDir)
    {
        if (string.IsNullOrEmpty(runtimeDir)) return null;
        try
        {
            string statusPath = Path.Combine(runtimeDir, "plamo_status.json");
            if (!File.Exists(statusPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(statusPath, Encoding.UTF8));
            string st = doc.RootElement.TryGetProperty("state", out var s) ? (s.GetString() ?? string.Empty) : string.Empty;
            string msg = doc.RootElement.TryGetProperty("message", out var m) ? (m.GetString() ?? string.Empty) : string.Empty;
            return (st, msg);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> DiscoverModelIdAsync(int port, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedRemoteModelId))
            return _cachedRemoteModelId;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromMilliseconds(1500));
            using var resp = await _http.GetAsync(BuildLoopbackUri(port, "/v1/models"), linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            string body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var list)
                && list.ValueKind == JsonValueKind.Array
                && list.GetArrayLength() > 0
                && list[0].TryGetProperty("id", out var idElem))
            {
                string? id = idElem.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    _cachedRemoteModelId = id;
                    return id;
                }
            }
        }
        catch
        {
            // Server still warming up
        }
        return null;
    }

    private async Task<(string? ModelId, string? Error)> WaitUntilPlamoReadyAsync(
        TranslationBackendKind backend,
        string? runtimeDir,
        Action<string> onStageUpdate,
        CancellationToken ct)
    {
        if (await DiscoverModelIdAsync(DefaultPlamoPort, ct).ConfigureAwait(false) is { } immediate)
            return (immediate, null);

        for (int tick = 0; tick < 300; tick++)
        {
            ct.ThrowIfCancellationRequested();

            if (ReadSidecarStatus(runtimeDir) is { } sidecar)
            {
                if (sidecar.State == "error")
                {
                    string errMsg = string.IsNullOrEmpty(sidecar.Message)
                        ? $"{backend.ShortName()} の起動に失敗しました"
                        : sidecar.Message;
                    return (null, errMsg);
                }
                onStageUpdate(string.IsNullOrEmpty(sidecar.Message)
                    ? $"{backend.ShortName()} を準備中…"
                    : sidecar.Message);
            }
            else if (tick == 0)
            {
                onStageUpdate($"{backend.ShortName()} ローカルサーバーへ接続中…");
            }

            await Task.Delay(600, ct).ConfigureAwait(false);
            if (await DiscoverModelIdAsync(DefaultPlamoPort, ct).ConfigureAwait(false) is { } readyId)
                return (readyId, null);
        }

        return (null, $"{backend.ShortName()} サーバーが応答しません（画面内のダウンロードカードまたは ⚙ メニューを確認してください）");
    }

    private async Task<TranslationOutcome> TranslateWithPlamoAsync(
        string cleanedText,
        string resolvedSource,
        string targetCode,
        TranslationBackendKind backend,
        string cacheKey,
        string? runtimeDir,
        Action<string> onStageUpdate,
        Action<string> onPartialStream,
        CancellationToken ct)
    {
        string? modelId;
        try
        {
            var ready = await WaitUntilPlamoReadyAsync(backend, runtimeDir, onStageUpdate, ct).ConfigureAwait(false);
            if (ready.Error is not null)
                return new TranslationOutcome.Failed(ready.Error);
            modelId = ready.ModelId!;
        }
        catch (OperationCanceledException)
        {
            return new TranslationOutcome.Failed("キャンセル");
        }

        var activeHistory = cleanedText.Length >= 25
            ? _historyTurns
            : (IReadOnlyList<Pfml3PromptBuilder.HistoryTurn>)Array.Empty<Pfml3PromptBuilder.HistoryTurn>();

        string prompt = Pfml3PromptBuilder.BuildPrompt(
            cleanedText,
            resolvedSource,
            targetCode,
            activeHistory);

        int maxTokens = Pfml3PromptBuilder.RecommendedMaxTokens(cleanedText);
        var payloadObj = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["prompt"] = prompt,
            ["temperature"] = 0.0,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["cache_prompt"] = true,
            ["stop"] = new[] { "<|plamo:tag|>", "<|plamo:bos|>", "<|plamo:eos|>" }
        };

        string jsonBody = JsonSerializer.Serialize(payloadObj);
        using var req = new HttpRequestMessage(HttpMethod.Post, BuildLoopbackUri(DefaultPlamoPort, "/v1/completions"))
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };

        onStageUpdate($"{backend.ShortName()} 翻訳中…（{cleanedText.Length}字）");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _cachedRemoteModelId = null;
                return new TranslationOutcome.Failed($"{backend.ShortName()} 推論応答エラー ({(int)resp.StatusCode})");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var accumulated = new StringBuilder();
            var fallbackLines = new List<string>();
            var sw = Stopwatch.StartNew();
            long lastEmitMs = 0;

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    var (chunk, isDone) = Pfml3PromptBuilder.ParseSseLine(line);
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        accumulated.Append(chunk);
                        if (sw.ElapsedMilliseconds - lastEmitMs >= 120)
                        {
                            string cleanPartial = Pfml3PromptBuilder.StripStopTokens(accumulated.ToString());
                            if (!string.IsNullOrEmpty(cleanPartial))
                            {
                                onPartialStream(cleanPartial);
                                onStageUpdate($"{backend.ShortName()} 生成中… {cleanPartial.Length}字");
                            }
                            lastEmitMs = sw.ElapsedMilliseconds;
                        }
                    }
                    if (isDone) break;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    fallbackLines.Add(line);
                }
            }

            if (accumulated.Length == 0 && fallbackLines.Count > 0)
            {
                string combined = string.Join("\n", fallbackLines);
                using var doc = JsonDocument.Parse(combined);
                if (doc.RootElement.TryGetProperty("choices", out var ch)
                    && ch.GetArrayLength() > 0
                    && ch[0].TryGetProperty("text", out var t))
                {
                    accumulated.Append(t.GetString());
                }
            }

            string finalOut = Pfml3PromptBuilder.StripStopTokens(accumulated.ToString());
            if (string.IsNullOrEmpty(finalOut))
                return new TranslationOutcome.Failed($"{backend.ShortName()} の翻訳結果が空でした");

            _cache.Set(cacheKey, finalOut);
            AppendHistory(resolvedSource, targetCode, cleanedText, finalOut);
            return new TranslationOutcome.Translated(finalOut, FromCache: false);
        }
        catch (OperationCanceledException)
        {
            return new TranslationOutcome.Failed("キャンセル");
        }
        catch (Exception ex)
        {
            _cachedRemoteModelId = null;
            return new TranslationOutcome.Failed($"{backend.ShortName()} 通信エラー: {ex.Message}");
        }
    }

    private async Task<TranslationOutcome> TranslateWithCustomOpenAiAsync(
        string cleanedText,
        string resolvedSource,
        string targetCode,
        string cacheKey,
        string? runtimeDir,
        Action<string> onStageUpdate,
        Action<string> onPartialStream,
        CancellationToken ct)
    {
        var settings = !string.IsNullOrEmpty(runtimeDir) ? AppUserSettings.Load(runtimeDir) : new AppUserSettings();
        Uri endpointUri = !string.IsNullOrWhiteSpace(settings.CustomOpenAiEndpoint) && Uri.TryCreate(settings.CustomOpenAiEndpoint, UriKind.Absolute, out var u)
            ? u
            : BuildLoopbackUri(1234, "/v1/chat/completions");

        string modelName = !string.IsNullOrWhiteSpace(settings.CustomOpenAiModel)
            ? settings.CustomOpenAiModel
            : "local-model";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelName,
            ["temperature"] = 0.0,
            ["stream"] = true,
            ["messages"] = new object[]
            {
                new Dictionary<string, string>
                {
                    ["role"] = "system",
                    ["content"] = $"Translate the user's text from {resolvedSource} into natural {targetCode}. Output ONLY the translated text without explanations."
                },
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = cleanedText
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        onStageUpdate($"外部ローカルAPI 翻訳中…（{cleanedText.Length}字）");

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new TranslationOutcome.Failed($"外部ローカルAPI 応答エラー ({(int)resp.StatusCode})");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var accumulated = new StringBuilder();
            var sw = Stopwatch.StartNew();
            long lastEmitMs = 0;

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var (chunk, isDone) = Pfml3PromptBuilder.ParseSseLine(line);
                if (!string.IsNullOrEmpty(chunk))
                {
                    accumulated.Append(chunk);
                    if (sw.ElapsedMilliseconds - lastEmitMs >= 120)
                    {
                        string partial = accumulated.ToString().Trim();
                        if (!string.IsNullOrEmpty(partial))
                            onPartialStream(partial);
                        lastEmitMs = sw.ElapsedMilliseconds;
                    }
                }
                if (isDone) break;
            }

            string finalOut = accumulated.ToString().Trim();
            if (string.IsNullOrEmpty(finalOut))
                return new TranslationOutcome.Failed("外部ローカルAPI の翻訳結果が空でした");

            _cache.Set(cacheKey, finalOut);
            return new TranslationOutcome.Translated(finalOut, FromCache: false);
        }
        catch (OperationCanceledException)
        {
            return new TranslationOutcome.Failed("キャンセル");
        }
        catch (Exception ex)
        {
            return new TranslationOutcome.Failed($"外部ローカルAPI 接続失敗（LM Studio / Ollama 等のポートを確認してください）: {ex.Message}");
        }
    }

    private void AppendHistory(string sourceLang, string targetLang, string sourceText, string translatedText)
    {
        if (sourceText.Length is < 30 or > 450 || translatedText.Length > 600)
            return;

        _historyTurns.Add(new Pfml3PromptBuilder.HistoryTurn(sourceLang, targetLang, sourceText, translatedText));
        if (_historyTurns.Count > 4)
            _historyTurns.RemoveRange(0, _historyTurns.Count - 4);
    }
}
