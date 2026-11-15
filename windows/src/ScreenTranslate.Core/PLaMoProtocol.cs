using System.Text;
using System.Text.Json;

namespace ScreenTranslate.Core;

public enum TranslationBackendKind
{
    Plamo2B,
    Plamo08B,
    Plamo8B,
    CustomOpenAI
}

public static class TranslationBackendExtensions
{
    public static string ToStorageId(this TranslationBackendKind kind) => kind switch
    {
        TranslationBackendKind.Plamo2B => "plamo-2b",
        TranslationBackendKind.Plamo08B => "plamo-0.8b",
        TranslationBackendKind.Plamo8B => "plamo-8b",
        TranslationBackendKind.CustomOpenAI => "openai-compat",
        _ => "plamo-2b"
    };

    public static TranslationBackendKind FromStorageId(string? id) => id switch
    {
        "plamo-0.8b" => TranslationBackendKind.Plamo08B,
        "plamo-8b" => TranslationBackendKind.Plamo8B,
        "openai-compat" => TranslationBackendKind.CustomOpenAI,
        _ => TranslationBackendKind.Plamo2B
    };

    public static string ShortName(this TranslationBackendKind kind) => kind switch
    {
        TranslationBackendKind.Plamo2B => "PLaMo 2B",
        TranslationBackendKind.Plamo08B => "PLaMo 0.8B",
        TranslationBackendKind.Plamo8B => "PLaMo 8B",
        TranslationBackendKind.CustomOpenAI => "外部ローカルAPI",
        _ => "PLaMo 2B"
    };

    public static string MenuTitle(this TranslationBackendKind kind) => kind switch
    {
        TranslationBackendKind.Plamo2B => "PLaMo 3 Translate 2B（推奨・GPU 1.8GB）",
        TranslationBackendKind.Plamo08B => "PLaMo 3 Translate 0.8B（超高速・GPU 0.6GB）",
        TranslationBackendKind.Plamo8B => "PLaMo 3 Translate 8B（最高精度・GPU 5.3GB）",
        TranslationBackendKind.CustomOpenAI => "OpenAI互換ローカルエンドポイント (LM Studio / Ollama等)",
        _ => "PLaMo 3 Translate 2B"
    };

    public static string? PlamoSizeArg(this TranslationBackendKind kind) => kind switch
    {
        TranslationBackendKind.Plamo2B => "2b",
        TranslationBackendKind.Plamo08B => "0.8b",
        TranslationBackendKind.Plamo8B => "8b",
        _ => null
    };

    public static bool IsPlamo(this TranslationBackendKind kind) => kind != TranslationBackendKind.CustomOpenAI;
}

/// <summary>
/// PLaMo 3 Translate 公式 PFML3 プロンプトビルダおよび OpenAI 互換 SSE ストリーム解析器。
/// </summary>
public static class Pfml3PromptBuilder
{
    public readonly record struct HistoryTurn(
        string SourceLang,
        string TargetLang,
        string SourceText,
        string TranslatedText);

    private static readonly string[] StopMarkers =
    [
        "<|plamo:tag|>",
        "<|plamo:bos|>",
        "<|plamo:eos|>",
        "<|endoftext|>"
    ];

    public static string PlamoLangCode(string appCode) => appCode switch
    {
        "zh-Hans" => "zh-hans",
        "zh-Hant" => "zh-hant",
        _ => appCode.ToLowerInvariant()
    };

    public static string BuildPrompt(
        string sourceText,
        string sourceLangCode,
        string targetLangCode,
        IReadOnlyList<HistoryTurn>? history = null,
        string? japaneseForm = null)
    {
        string srcCode = PlamoLangCode(sourceLangCode);
        string tgtCode = PlamoLangCode(targetLangCode);

        var sb = new StringBuilder(512);
        sb.Append("<|plamo:tag|>dataset<|plamo:msg|>translation");

        if (history is { Count: > 0 })
        {
            var matching = history
                .Where(h => PlamoLangCode(h.SourceLang) == srcCode && PlamoLangCode(h.TargetLang) == tgtCode)
                .TakeLast(2);

            foreach (var turn in matching)
            {
                string cleanSrc = SanitizeControlTokens(turn.SourceText);
                string cleanTgt = SanitizeControlTokens(turn.TranslatedText);
                if (!string.IsNullOrEmpty(cleanSrc) && !string.IsNullOrEmpty(cleanTgt))
                {
                    sb.Append("<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>").Append(srcCode)
                      .Append("<|plamo:msg|>").Append(cleanSrc);
                    sb.Append("<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>").Append(tgtCode)
                      .Append("<|plamo:msg|>").Append(cleanTgt);
                }
            }
        }

        string cleanCurrent = SanitizeControlTokens(sourceText);
        sb.Append("<|plamo:tag|>input<|plamo:key|>lang<|plamo:val|>").Append(srcCode)
          .Append("<|plamo:msg|>").Append(cleanCurrent);
        sb.Append("<|plamo:tag|>output<|plamo:key|>lang<|plamo:val|>").Append(tgtCode);

        if (tgtCode == "ja" && (japaneseForm == "plain" || japaneseForm == "polite"))
        {
            sb.Append("<|plamo:key|>form<|plamo:val|>").Append(japaneseForm);
        }

        sb.Append("<|plamo:msg|>");
        return sb.ToString();
    }

    public static string StripStopTokens(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        string s = raw;
        foreach (string marker in StopMarkers)
        {
            int idx = s.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                s = s[..idx];
        }
        return s.Trim();
    }

    public static int RecommendedMaxTokens(string sourceText)
    {
        int count = sourceText?.Length ?? 0;
        return Math.Min(2048, Math.Max(160, count * 2 + 96));
    }

    public static (string? Chunk, bool IsDone) ParseSseLine(string line)
    {
        string trimmed = line.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
            return (null, false);

        string payload = trimmed[5..].Trim();
        if (payload == "[DONE]")
            return (null, true);

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return (null, false);
            }

            var first = choices[0];
            bool finished = first.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(fr.GetString());

            if (first.TryGetProperty("text", out var txtElem) && txtElem.ValueKind == JsonValueKind.String)
                return (txtElem.GetString(), finished);

            if (first.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var contentElem)
                && contentElem.ValueKind == JsonValueKind.String)
            {
                return (contentElem.GetString(), finished);
            }

            return (null, finished);
        }
        catch
        {
            return (null, false);
        }
    }

    private static string SanitizeControlTokens(string text)
        => (text ?? string.Empty).Replace("<|plamo:", string.Empty, StringComparison.Ordinal).Trim();
}
