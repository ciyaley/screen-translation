using System.Diagnostics;
using ScreenTranslate.Core;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"FAIL: {message}");
        Environment.Exit(1);
    }
}

static byte[] CreatePatternBuffer(int width, int height, Func<int, int, byte> pattern)
{
    int stride = width * 4;
    var buf = new byte[height * stride];
    for (int y = 0; y < height; y++)
    {
        int row = y * stride;
        for (int x = 0; x < width; x++)
        {
            byte v = pattern(x, y);
            buf[row + x * 4 + 0] = v;
            buf[row + x * 4 + 1] = v;
            buf[row + x * 4 + 2] = v;
            buf[row + x * 4 + 3] = 255;
        }
    }
    return buf;
}

// 1. TextStabilityGate & IMPORTANT_UPDATE 検証
var gate = new TextStabilityGate(requiredStableFrames: 2);
Check(gate.Feed("The northern") == StabilityAction.Provisional("The northern"), "短文1フレーム目はProvisional");
Check(gate.Feed("The northern gate is sealed.") == StabilityAction.Provisional("The northern gate is sealed."), "到達直後はProvisional");
Check(gate.Feed("The northern gate is seaIed.") == StabilityAction.Commit("The northern gate is seaIed."), "1文字OCR揺れでも2フレーム目でCommit");
Check(gate.Feed("The northern gate is sealed.") == StabilityAction.Ignore(), "確定後の微揺れはIgnore");
Check(gate.Feed("The northern gate is not sealed.") == StabilityAction.Provisional("The northern gate is not sealed."), "否定語追加(IMPORTANT_UPDATE)を検知");
Check(gate.Feed("The northern gate is not sealed.") == StabilityAction.Commit("The northern gate is not sealed."), "否定語追加の2フレーム目でCommit");

string longParagraph = "The ancient sacred tree rooted in the Kingdom of Karen, bestowing blessings upon people, is the faith of the entire nation.";
Check(gate.Feed(longParagraph, 0.91f) == StabilityAction.Commit(longParagraph), "60文字以上の段落は初回フレーム即Commit");

// 2. BoundedLruCache 検証
var cache = new BoundedLruCache<string, string>(8);
for (int i = 0; i < 10; i++)
    cache.Set($"k{i}", $"v{i}");
Check(!cache.TryGet("k0", out _) && !cache.TryGet("k1", out _), "古いLRUエントリが破棄されること");
Check(cache.TryGet("k9", out var v9) && v9 == "v9", "最新LRUエントリが保持されること");

// 3. 512点輝度比較 + FrameChangeGate + ベンチマーク検証
var buf1 = CreatePatternBuffer(64, 32, (x, _) => (byte)Math.Clamp(x * 4, 0, 255));
var bufNoise = CreatePatternBuffer(64, 32, (x, y) => (byte)Math.Clamp(x * 4 + ((x + y) % 2 == 0 ? 2 : -1), 0, 255));
var buf2 = CreatePatternBuffer(64, 32, (x, _) => (byte)Math.Clamp((63 - x) * 4, 0, 255));
var bufHd = CreatePatternBuffer(960, 540, (x, y) => (byte)((x + y) & 0xFF));

var d1 = FrameHash.ComputeFromBgra32(buf1, 64, 32, 64 * 4)!.Value;
var dNoise = FrameHash.ComputeFromBgra32(bufNoise, 64, 32, 64 * 4)!.Value;
var d2 = FrameHash.ComputeFromBgra32(buf2, 64, 32, 64 * 4)!.Value;
Check(FrameHash.IsEffectivelyUnchanged(in d1, in dNoise), "微小圧縮ノイズで誤発火しないこと");
Check(!FrameHash.IsEffectivelyUnchanged(in d1, in d2), "画面変化を確実に検知すること");

var sw = Stopwatch.StartNew();
ulong sink = 0;
for (int i = 0; i < 2000; i++)
{
    var d = FrameHash.ComputeFromBgra32(bufHd, 960, 540, 960 * 4)!.Value;
    sink ^= d.Lo;
}
double perFrameUs = (sw.Elapsed.TotalMilliseconds * 1000.0) / 2000.0;
Check(perFrameUs < 200.0 && sink != 0xDEADBEEFCAFEBABEUL, $"32BGRA 512点輝度比較が極小負荷(<200us)であること ({perFrameUs:F2}us)");

var changeGate = new FrameChangeGate();
Check(changeGate.ShouldRunOcr(in d1) == true, "初回フレームは即実行");
Check(changeGate.ShouldRunOcr(in dNoise) == false, "静止中はOCRゼロ");
Check(changeGate.ShouldRunOcr(in d2) == true, "画面移動中は即座にOCR実行");

// 4. OcrSpatialFilter（Windows左上原点 NormTopY・見出し横装飾 'co' 除去・折り返し結合・複数クラスタ分離）検証
OcrTextObservation[] multiScene =
[
    new("Spirit of the Sacred Tree", 0.95f, 0.20, 0.12, 0.36, 0.06),
    new("co", 0.78f, 0.60, 0.12, 0.04, 0.05),
    new("The ancient tower", 0.94f, 0.10, 0.34, 0.42, 0.04),
    new("awaits your return.", 0.92f, 0.10, 0.40, 0.44, 0.04),
    new("A nun deeply devoted to the sacred tree.", 0.93f, 0.10, 0.78, 0.60, 0.04),
    new("120 FPS", 0.96f, 0.82, 0.06, 0.14, 0.04),
    new("AUTO SKIP", 0.91f, 0.80, 0.92, 0.16, 0.04)
];
var extracted = OcrSpatialFilter.ExtractPrimaryText(multiScene, "auto", "ja");
Check(extracted.Clusters.Count == 3, $"3クラスタに分離されること (got {extracted.Clusters.Count})");
Check(extracted.Clusters[0].Text == "Spirit of the Sacred Tree", $"見出し横の唐草誤読 'co' が除去されること (got {extracted.Clusters[0].Text})");
Check(extracted.Clusters[1].Text == "The ancient tower awaits your return.", $"折り返し行がスペース結合されること (got {extracted.Clusters[1].Text})");
Check(extracted.Clusters[2].Text == "A nun deeply devoted to the sacred tree.", "下段クラスタが独立保持されること");

// 5. ClusterGeometry（Windows左上原点座標系）& ClusterTranslationPlanner 検証
var capRect = new ScreenRect(200, 100, 1000, 800);
var bottomBounds = ClusterGeometry.ToScreenRect(extracted.Clusters[2], capRect);
Check(Math.Abs(bottomBounds.X - 300.0) < 1.0 && Math.Abs(bottomBounds.Y - 724.0) < 1.0, $"Windows左上原点スクリーン座標へ正確に変換されること ({bottomBounds})");

var normCursor = ClusterGeometry.NormalizedCursorPoint(400, 735, capRect);
Check(normCursor is not null, "領域内カーソル座標が正規化されること");

var spotSelected = ClusterTranslationPlanner.SelectActiveClusters(extracted.Clusters, TranslationDisplayMode.CursorSpot, normCursor);
Check(spotSelected.Count == 1 && spotSelected[0].Text == "A nun deeply devoted to the sacred tree.", "近傍のみモードでカーソル直下の1件が選ばれること");

var prioritized = ClusterTranslationPlanner.PrioritizedIndices(extracted.Clusters, [0, 1, 2], normCursor);
Check(prioritized[0] == 2, $"カーソル直下の未翻訳クラスタが最優先になること ({string.Join(",", prioritized)})");

var historyCache = new[]
{
    new ClusterTranslationPlanner.CachedEntry("A nun deeply devoted to the sacred tree.", "聖樹に深く帰依する修道女。")
};
var scrolledCluster = new ExtractedTextCluster("A nun deeply devoted to the sacred tree", 0.90f, 0.10, 0.70, 0.42, 0.46);
Check(ClusterTranslationPlanner.FindCachedTranslation(scrolledCluster, historyCache) == "聖樹に深く帰依する修道女。", "スクロール移動時も0msでキャッシュヒットすること");

Check(ClusterGeometry.ShouldFadeChipForCursor(new ScreenRect(300, 700, 240, 48), 350, 720) == true, "カーソル接近時にインプレースチップが透過退避すること");
Check(ClusterGeometry.ShouldFadeChipForCursor(new ScreenRect(300, 700, 240, 48), 100, 200) == false, "カーソル離脱時は不透明維持すること");

// 6. TranslatabilityGate & PFML3 検証
Check(TranslatabilityGate.Evaluate("HP 450 / 500", "auto", "ja") == new GateDecision.Skip(SkipReason.NumericOrStatsOnly), "HP数値はスキップ");
Check(TranslatabilityGate.Evaluate("[E]", "auto", "ja") == new GateDecision.Skip(SkipReason.KeybindOrHudControl), "[E]キー表記はスキップ");
string sampleUrl = "https" + "://app.local/v1/status";
Check(TranslatabilityGate.Evaluate(sampleUrl, "auto", "ja") == new GateDecision.Skip(SkipReason.CodeOrIdentifier), "URLはスキップ");
Check(TranslatabilityGate.Evaluate("すでに日本語で書かれた文章です。", "auto", "ja") == new GateDecision.Skip(SkipReason.AlreadyTargetLanguage), "翻訳先言語はスキップ");

var pfmlHistory = new[]
{
    new Pfml3PromptBuilder.HistoryTurn("en", "ja", "The Sacred Tree Festival begins today.", "本日、神樹祭が始まる。")
};
string pfml = Pfml3PromptBuilder.BuildPrompt("Hekate attends the festival with Ela.", "en", "ja", pfmlHistory);
string expectedPfml =
    "<|plamo:tag|>dataset<|plamo:msg|>translation" +
    "<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>en<|plamo:msg|>The Sacred Tree Festival begins today." +
    "<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>ja<|plamo:msg|>本日、神樹祭が始まる。" +
    "<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>en<|plamo:msg|>Hekate attends the festival with Ela." +
    "<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>ja<|plamo:msg|>";
Check(pfml == expectedPfml, "PFML3プロンプトが公式仕様どおり生成されること");

var sse = Pfml3PromptBuilder.ParseSseLine("data: {\"choices\":[{\"text\":\"ヘカテはエラと共に\",\"finish_reason\":null}]}");
Check(sse.Chunk == "ヘカテはエラと共に" && !sse.IsDone, "SSEチャンク解析が機能すること");
Check(Pfml3PromptBuilder.ParseSseLine("data: [DONE]").IsDone, "SSE [DONE] が終端判定されること");
Check(Pfml3PromptBuilder.StripStopTokens("ヘカテは祭りに参加する。<|plamo:tag|>extra") == "ヘカテは祭りに参加する。", "ストップトークン除去が機能すること");

// 7. NativeSidecarManager（内蔵ダウンローダーURL解決・GPUアセット選択・進捗表示）検証
var spec2b = NativeSidecarManager.GetSpec(TranslationBackendKind.Plamo2B)!.Value;
var hfUri = NativeSidecarManager.BuildHuggingFaceResolveUri(spec2b.RepoId, spec2b.GgufFileName);
Check(hfUri.Host == "huggingface.co" && hfUri.AbsolutePath == "/pfnet/plamo-3-translate-2b-GGUF/resolve/main/plamo-3-translate-2b-Q4_K_M.gguf", $"HuggingFace GGUF URLが正確であること ({hfUri})");

(string Name, string Url, long Size)[] mockAssets =
[
    ("cudart-llama-bin-win-cuda-12.4-x64.zip", "u1", 400_000_000L),
    ("llama-b8500-bin-win-cuda-12.4-x64.zip", "u2", 65_000_000L),
    ("llama-b8500-bin-win-vulkan-x64.zip", "u3", 30_000_000L)
];
var vkPicked = NativeSidecarManager.SelectEngineAssets(mockAssets, GpuEngineFlavor.Vulkan, out string vkDir);
Check(vkPicked.Count == 1 && vkDir == "vulkan" && vkPicked[0].Url == "u3", "Vulkan GPUランタイムが選択されること");
var cudaPicked = NativeSidecarManager.SelectEngineAssets(mockAssets, GpuEngineFlavor.Cuda, out string cudaDir);
Check(cudaPicked.Count == 2 && cudaDir == "cuda", "CUDAランタイムはcudartと本体の両方が選択されること");

string progStr = NativeSidecarManager.FormatProgressSummary(new SidecarStatusSnapshot(
    SidecarState.Downloading, TranslationBackendKind.Plamo2B, "DL中", 0.5, 905_000_000L, 1_810_000_000L, 52_428_800.0));
Check(progStr.Contains("50%") && progStr.Contains("GB") && progStr.Contains("MB/s"), $"進捗文字列が整形されること ({progStr})");

Console.WriteLine($"ALL WINDOWS CORE VERIFICATION CHECKS PASSED (32BGRA 512-pt dHash={perFrameUs:F2}us, MULTI-CLUSTER PLANNER, IN-PLACE GEOMETRY, NATIVE DOWNLOADER, AND PLAMO 3 PFML3)");
