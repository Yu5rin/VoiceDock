namespace VoiceDock.Models;

/// <summary>音声認識の言語。Web Speech API に渡す言語コードと、画面に出す名前。</summary>
public static class RecognitionLanguages
{
    public const string Japanese = "ja-JP";
    public const string English = "en-US";

    /// <summary>選べる言語の一覧（画面の並び順）。</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> All = new[]
    {
        (Japanese, "日本語"),
        (English, "英語（English）"),
    };

    public static bool IsSupported(string? code) => All.Any(l => l.Code == code);

    public static string LabelOf(string? code) =>
        All.FirstOrDefault(l => l.Code == code).Label ?? "日本語";

    /// <summary>単語をスペースで区切る言語かどうか（英語など）。</summary>
    public static bool UsesWordSpacing(string? code) => code == English;
}
