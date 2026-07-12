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
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string LogsDir => Path.Combine(Root, "logs");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
