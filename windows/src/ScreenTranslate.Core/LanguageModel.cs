namespace ScreenTranslate.Core;

public readonly record struct TranslationLanguage(string Code, string ShortName)
{
    public static readonly TranslationLanguage Auto = new("auto", "自動");

    public static readonly IReadOnlyList<TranslationLanguage> ConcreteLanguages =
    [
        new("en", "英語"),
        new("ja", "日本語"),
        new("zh-Hans", "中国語(簡)"),
        new("zh-Hant", "中国語(繁)"),
        new("ko", "韓国語"),
        new("fr", "仏語"),
        new("de", "独語"),
        new("es", "西語"),
        new("ru", "露語")
    ];

    public static readonly IReadOnlyList<TranslationLanguage> SourceOptions =
        new[] { Auto }.Concat(ConcreteLanguages).ToArray();

    public static readonly IReadOnlyList<TranslationLanguage> TargetOptions =
        ConcreteLanguages;

    /// <summary>
    /// Windows.Media.Ocr 用の BCP-47 言語タグへ変換する。
    /// </summary>
    public static string ToWindowsBcp47Tag(string code) => code switch
    {
        "en" => "en-US",
        "ja" => "ja-JP",
        "zh-Hans" => "zh-Hans-CN",
        "zh-Hant" => "zh-Hant-TW",
        "ko" => "ko-KR",
        "fr" => "fr-FR",
        "de" => "de-DE",
        "es" => "es-ES",
        "ru" => "ru-RU",
        _ => "en-US"
    };

    public static string ResolveSourceCode(string selectedCode, string sampleText, string targetCode)
    {
        if (selectedCode != "auto")
            return selectedCode;

        var decision = TranslatabilityGate.Evaluate(sampleText, "auto", targetCode);
        return decision switch
        {
            GateDecision.Translate t => t.ResolvedSourceCode,
            GateDecision.Skip s when s.Reason == SkipReason.AlreadyTargetLanguage => targetCode,
            _ => targetCode == "ja" ? "en" : "ja"
        };
    }
}

public abstract record TranslationOutcome
{
    private TranslationOutcome() { }
    public sealed record Translated(string Text, bool FromCache) : TranslationOutcome;
    public sealed record Skipped(SkipReason Reason) : TranslationOutcome;
    public sealed record Failed(string Message) : TranslationOutcome;
}
