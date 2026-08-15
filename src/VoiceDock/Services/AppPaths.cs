using System.IO;

namespace VoiceDock.Services;

/// <summary>
/// %APPDATA%\VoiceDock 配下のパス定義。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceDock");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DictionaryFile => Path.Combine(Root, "dictionary.json");
    public static string SnippetsFile => Path.Combine(Root, "snippets.json");
    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>認識用ブラウザ(Edge)の専用プロファイル。マイク許可等を保持する。</summary>
    public static string BrowserProfileDir => Path.Combine(Root, "browser-profile");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }
}
