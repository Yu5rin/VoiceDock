using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace VoiceDock.Services;

/// <summary>
/// 認識用ブラウザ(Microsoft Edge、無ければ Chrome)を起動・管理する。
/// アプリモードで専用プロファイルを使い、画面外に常駐させて認識ページを読み込む。
/// マイク許可はコマンドラインで自動付与し、バックグラウンドでも認識が止まらないよう
/// スロットリング抑制フラグを付ける。
/// </summary>
public sealed class BrowserLauncher : IDisposable
{
    private readonly LogService _log;
    private Process? _process;
    private string? _url;

    public BrowserLauncher(LogService log)
    {
        _log = log;
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

    /// <summary>Edge/Chrome を起動して認識ページを開く。起動できたら true。</summary>
    public bool Launch(string url)
    {
        _url = url;
        var exe = FindEdge() ?? FindChrome();
        if (exe == null)
        {
            _log.Error("認識用ブラウザ (Microsoft Edge / Chrome) が見つかりませんでした");
            return false;
        }

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
            _log.Info($"認識用ブラウザを起動しました: {Path.GetFileName(exe)}");
            return _process != null;
        }
        catch (Exception ex)
        {
            _log.Error($"認識用ブラウザの起動に失敗しました: {ex.Message}");
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
