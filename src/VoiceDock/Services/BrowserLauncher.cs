using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// 認識用ブラウザ(Chromium 系)を起動・管理する。
/// 既定では Windows の既定ブラウザに追従し（Chrome 系なら Chrome、それ以外は Edge）、
/// 設定で明示指定もできる。アプリモードで専用プロファイルを使い、画面外に常駐させて
/// 認識ページを読み込む。マイク許可はコマンドラインで自動付与し、バックグラウンドでも
/// 認識が止まらないようスロットリング抑制フラグを付ける。
/// </summary>
public sealed class BrowserLauncher : IDisposable
{
    private readonly LogService _log;
    private readonly SettingsService _settings;
    private Process? _process;
    private string? _url;

    /// <summary>実際に起動したブラウザの表示名（未起動なら null）。</summary>
    public string? LaunchedBrowserName { get; private set; }

    public BrowserLauncher(LogService log, SettingsService settings)
    {
        _log = log;
        _settings = settings;
    }

    /// <summary>認識用ブラウザのプロセスが生存しているか。</summary>
    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>前回と同じ URL でブラウザを再起動する（ウォッチドッグ用）。</summary>
    public bool Relaunch()
    {
        if (_url == null) return false;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* 終了失敗は無視して再起動を試みる */ }
        _process?.Dispose();
        _process = null;
        _log.Warn("認識用ブラウザが停止していたため再起動します");
        return Launch(_url);
    }

    /// <summary>設定に応じたブラウザを起動して認識ページを開く。起動できたら true。</summary>
    public bool Launch(string url)
    {
        _url = url;
        var (exe, name) = ResolveBrowser();
        if (exe == null)
        {
            _log.Error("認識用ブラウザ (Microsoft Edge / Google Chrome) が見つかりませんでした");
            return false;
        }
        LaunchedBrowserName = name;

        Directory.CreateDirectory(AppPaths.BrowserProfileDir);

        var args = new List<string>
        {
            $"--app={url}",
            $"--user-data-dir={AppPaths.BrowserProfileDir}",
            "--no-first-run",
            "--no-default-browser-check",
            // 認識ページ(127.0.0.1)へのマイク許可を自動付与（専用プロファイルの隔離インスタンスのみ）
            "--use-fake-ui-for-media-stream",
            // 画面外・非表示でも認識を止めないためのスロットリング抑制
            "--disable-background-timer-throttling",
            "--disable-renderer-backgrounding",
            "--disable-backgrounding-occluded-windows",
            "--disable-features=CalculateNativeWinOcclusion",
            // 画面外に小さく配置して事実上見えないようにする
            "--window-size=220,120",
            "--window-position=-32000,-32000",
        };

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _process = Process.Start(psi);
            _log.Info($"認識用ブラウザを起動しました: {name} ({Path.GetFileName(exe)})");
            return _process != null;
        }
        catch (Exception ex)
        {
            _log.Error($"認識用ブラウザの起動に失敗しました: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 設定に応じて使用するブラウザの実行ファイルと表示名を決める。
    /// Auto の場合は Windows の既定ブラウザに追従し、見つからなければもう一方へフォールバックする。
    /// </summary>
    private (string? Exe, string? Name) ResolveBrowser()
    {
        var choice = _settings.Current.Browser;

        if (choice == BrowserChoice.Chrome)
        {
            var chrome = FindChrome();
            if (chrome != null) return (chrome, "Google Chrome");
            _log.Warn("Google Chrome が見つからないため Microsoft Edge を使用します");
            return (FindEdge(), "Microsoft Edge");
        }

        if (choice == BrowserChoice.Edge)
        {
            var edge = FindEdge();
            if (edge != null) return (edge, "Microsoft Edge");
            _log.Warn("Microsoft Edge が見つからないため Google Chrome を使用します");
            return (FindChrome(), "Google Chrome");
        }

        // Auto: 既定ブラウザが Chrome なら Chrome、それ以外（Edge 等）は Edge を使う。
        // Web Speech API は Chromium 系でのみ動作するため、Firefox 等が既定の場合も Edge を使う。
        bool defaultIsChrome = IsDefaultBrowserChrome();
        if (defaultIsChrome)
        {
            var chrome = FindChrome();
            if (chrome != null)
            {
                _log.Info("既定ブラウザが Chrome のため Google Chrome で認識します");
                return (chrome, "Google Chrome");
            }
        }

        var edgeExe = FindEdge();
        if (edgeExe != null)
        {
            _log.Info("Microsoft Edge で認識します");
            return (edgeExe, "Microsoft Edge");
        }

        var chromeExe = FindChrome();
        if (chromeExe != null)
        {
            _log.Info("Edge が見つからないため Google Chrome で認識します");
            return (chromeExe, "Google Chrome");
        }
        return (null, null);
    }

    /// <summary>
    /// Windows の既定ブラウザ（https の関連付け ProgId）が Chrome かどうかを判定する。
    /// 判定できない場合は false（＝Edge を使う）。
    /// </summary>
    private static bool IsDefaultBrowserChrome()
    {
        try
        {
            const string key = @"SOFTWARE\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice";
            using var userChoice = Registry.CurrentUser.OpenSubKey(key);
            var progId = userChoice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return false;
            // 例: ChromeHTML, ChromeHTML.XXXX（Chrome）/ MSEdgeHTM（Edge）
            return progId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                   && !progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindEdge()
    {
        return FromAppPaths("msedge.exe")
               ?? FirstExisting(
                   Path.Combine(GetEnv("ProgramFiles(x86)"), @"Microsoft\Edge\Application\msedge.exe"),
                   Path.Combine(GetEnv("ProgramFiles"), @"Microsoft\Edge\Application\msedge.exe"));
    }

    private static string? FindChrome()
    {
        return FromAppPaths("chrome.exe")
               ?? FirstExisting(
                   Path.Combine(GetEnv("ProgramFiles"), @"Google\Chrome\Application\chrome.exe"),
                   Path.Combine(GetEnv("ProgramFiles(x86)"), @"Google\Chrome\Application\chrome.exe"),
                   Path.Combine(GetEnv("LocalAppData"), @"Google\Chrome\Application\chrome.exe"));
    }

    /// <summary>App Paths レジストリから実行ファイルのフルパスを引く。</summary>
    private static string? FromAppPaths(string exeName)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(keyPath + exeName);
            if (key?.GetValue(null) is string path && File.Exists(path))
                return path;
        }
        return null;
    }

    private static string GetEnv(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 終了処理の失敗は無視
        }
        _process?.Dispose();
    }
}
