using System.IO;

namespace VoiceDock.Services;

/// <summary>
/// 設定・辞書・定型文・ログなどの保存場所。
///
/// exe と同じフォルダの data フォルダに保存する（PC を引っ越すときに、exe と data フォルダを
/// まとめてコピーすれば、設定ごと持っていけるようにするため）。
/// exe の置き場所に書き込めない場合（Program Files に置いた等）だけ、以前と同じ %APPDATA%\VoiceDock を使う。
///
/// 以前の版は %APPDATA%\VoiceDock に保存していた。data フォルダがまだ無ければ、起動時に
/// 設定・辞書・定型文・ログを data フォルダへ写す（MigrateFromLegacy）。元のフォルダは消さない。
/// </summary>
public static class AppPaths
{
    /// <summary>以前の版の保存場所（%APPDATA%\VoiceDock）。</summary>
    public static string LegacyRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceDock");

    /// <summary>テストで保存場所を差し替えるための口。アプリの動作では常に null。</summary>
    internal static string? TestRoot = null;

    private static string? _root;

    /// <summary>保存場所。最初に使うときに、exe の隣の data フォルダに書き込めるかを確かめて決める。</summary>
    public static string Root => TestRoot ?? (_root ??= ResolveRoot());

    /// <summary>exe の隣の data フォルダに保存しているか（書き込めず %APPDATA% を使っている場合は false）。</summary>
    public static bool IsPortable => Root != LegacyRoot;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DictionaryFile => Path.Combine(Root, "dictionary.json");
    public static string SnippetsFile => Path.Combine(Root, "snippets.json");
    public static string LogsDir => Path.Combine(Root, "logs");

    /// <summary>認識用ブラウザ(Edge)の専用プロファイル。マイク許可等を保持する。</summary>
    public static string BrowserProfileDir => Path.Combine(Root, "browser-profile");

    /// <summary>以前の版の認識用ブラウザのプロファイル（残っているブラウザを片付けるときに使う）。</summary>
    public static string LegacyBrowserProfileDir => Path.Combine(LegacyRoot, "browser-profile");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }

    private static string ResolveRoot()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var data = Path.Combine(exeDir, "data");
            if (CanWrite(data)) return data;
        }
        return LegacyRoot;
    }

    /// <summary>フォルダを作り、実際にファイルを書いて消せるかで判断する（権限の設定を読むより確実なため）。</summary>
    internal static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以前の版の保存場所から、設定・辞書・定型文・ログを写す。写したファイルの数を返す。
    /// data フォルダにまだ設定ファイルが無いときだけ行い、上書きはしない。
    /// 認識用ブラウザのプロファイルは大きく、作り直しても困らないため写さない。
    /// </summary>
    public static int MigrateFromLegacy() => MigrateFrom(LegacyRoot, Root);

    internal static int MigrateFrom(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from).TrimEnd('\\', '/'), Path.GetFullPath(to).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)) return 0;
        if (!Directory.Exists(from)) return 0;
        if (File.Exists(Path.Combine(to, "settings.json"))) return 0;

        int copied = 0;
        Directory.CreateDirectory(to);
        foreach (var name in new[] { "settings.json", "dictionary.json", "snippets.json" })
        {
            foreach (var file in new[] { name, name + ".bak" })
                copied += CopyIfMissing(Path.Combine(from, file), Path.Combine(to, file));
        }

        var legacyLogs = Path.Combine(from, "logs");
        if (Directory.Exists(legacyLogs))
        {
            var logs = Path.Combine(to, "logs");
            Directory.CreateDirectory(logs);
            foreach (var file in Directory.GetFiles(legacyLogs))
                copied += CopyIfMissing(file, Path.Combine(logs, Path.GetFileName(file)));
        }
        return copied;
    }

    private static int CopyIfMissing(string source, string target)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(target)) return 0;
            File.Copy(source, target);
            return 1;
        }
        catch
        {
            // 写せなかったファイルは以前の場所に残っている。起動は続ける
            return 0;
        }
    }
}
