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
    /// <summary>この時間内に <see cref="MaxRelaunchesPerWindow"/> 回までしか再起動しない。</summary>
    private static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 一定時間内に許す再起動の回数。
    ///
    /// 「調子が戻ったら回数を 0 に戻す」方式にすると、
    /// 「起動 → 少し動く → 落ちる」を繰り返す状態で毎回リセットされ、
    /// 上限が働かないまま延々とウィンドウを開き続けてしまう。
    /// そのため回数ではなく「直近◯分に何回起動したか」で判断する。
    /// </summary>
    private const int MaxRelaunchesPerWindow = 5;

    private readonly LogService _log;
    private readonly SettingsService _settings;
    private Process? _process;
    private Func<string>? _urlFactory;

    /// <summary>直近の再起動時刻。古いものは順に捨てる。</summary>
    private readonly Queue<DateTime> _relaunchTimes = new();

    /// <summary>再起動の上限に達したかどうか（呼び出し側が監視を止めるために見る）。</summary>
    public bool GaveUp
    {
        get
        {
            TrimRelaunchHistory();
            return _relaunchTimes.Count >= MaxRelaunchesPerWindow;
        }
    }

    private void TrimRelaunchHistory()
    {
        var limit = DateTime.UtcNow - RelaunchWindow;
        while (_relaunchTimes.Count > 0 && _relaunchTimes.Peek() < limit)
            _relaunchTimes.Dequeue();
    }

    /// <summary>設定変更などで、諦めた状態からやり直せるようにする。</summary>
    public void ResetRelaunchAttempts() => _relaunchTimes.Clear();

    /// <summary>
    /// 使用ブラウザの設定を変えたときなどに、今のブラウザを終了して起動し直す。
    /// 再起動を諦めた状態からでも復帰できるよう、失敗回数はリセットする。
    /// </summary>
    public bool Restart()
    {
        _relaunchTimes.Clear();
        if (_urlFactory == null) return false;
        KillCurrent();
        _log.Info("設定の変更にあわせて認識用ブラウザを起動し直します");
        return Launch(_urlFactory);
    }

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
        if (_urlFactory == null) return false;
        if (GaveUp) return false;
        _relaunchTimes.Enqueue(DateTime.UtcNow);
        KillCurrent();
        _log.Warn($"認識用ブラウザが応答しないため再起動します" +
                  $"（直近 {RelaunchWindow.TotalMinutes:0} 分で {_relaunchTimes.Count}/{MaxRelaunchesPerWindow} 回目）");
        // 認識ページのトークンは使い捨てのため、再起動時は新しい URL を発行する
        bool ok = Launch(_urlFactory);
        if (!ok && GaveUp)
            _log.Error("認識用ブラウザを繰り返し起動できなかったため、自動再起動を停止しました");
        return ok;
    }

    /// <summary>今起動しているブラウザを終了させる。</summary>
    private void KillCurrent()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* 終了失敗は無視して起動を試みる */ }
        _process?.Dispose();
        _process = null;
    }

    /// <summary>
    /// 認識用プロファイルで動いている Chromium 本体を終了させる。
    ///
    /// Process.Start が返すプロセスは、既存インスタンスへ処理を渡して
    /// すぐ終了してしまうことがあり、その場合こちらの手元には本体のハンドルが残らない。
    /// Chromium 自身が二重起動の判定に使っているメッセージ専用ウィンドウ
    /// （クラス名 Chrome_MessageWindow・ウィンドウ文字列はプロファイルのパス）から
    /// 本体を突き止めて終了させる。専用プロファイルのみが対象なので、
    /// 利用者が普段使っているブラウザには影響しない。
    /// </summary>
    private void KillProfileInstance()
    {
        try
        {
            var profile = AppPaths.BrowserProfileDir;
            for (int i = 0; i < 5; i++)
            {
                var hwnd = FindWindowEx(HwndMessage, IntPtr.Zero, "Chrome_MessageWindow", profile);
                if (hwnd == IntPtr.Zero) return;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return;

                using var proc = Process.GetProcessById((int)pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
                _log.Info("認識用プロファイルで動いていたブラウザを終了しました");
            }
        }
        catch
        {
            // 見つからない・既に終了している場合は何もしない
        }
    }

    private static readonly IntPtr HwndMessage = new(-3);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>設定に応じたブラウザを起動して認識ページを開く。起動できたら true。</summary>
    /// <param name="urlFactory">
    /// 読み込ませる URL を生成する関数。認識ページのトークンは 1 回限り有効なため、
    /// 再起動のたびに新しい URL を発行できるよう関数で受け取る。
    /// </param>
    public bool Launch(Func<string> urlFactory)
    {
        _urlFactory = urlFactory;

        // 同じ --user-data-dir で既にブラウザが動いていると、ここで起動したプロセスは
        // 「既存のインスタンスに開いてもらう」よう頼んで即座に終了する。
        // その状態を放置すると、プロセスが終了したことを監視が「落ちた」と誤解して
        // 再起動を繰り返し、認識用ウィンドウだけが増え続ける。
        // 起動前に必ず片付けて、常に自分が本体を握るようにする。
        KillProfileInstance();

        var url = urlFactory();
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
            if (_process != null)
            {
                // ここでカウンタを戻すと、「起動はするがすぐ落ちる」状態のときに
                // 毎回リセットされ、上限が働かないまま永久に再起動を繰り返してしまう。
                // 実際に認識ブリッジへ接続できた時点（NotifyHealthy）で戻す。
                // 異常終了時に取り残されたブラウザを次回起動で片付けられるよう PID を残す
                SaveLaunchedPid(_process);
            }
            _log.Info($"認識用ブラウザを起動しました: {name} ({Path.GetFileName(exe)})");

            // 起動したプロセスがすぐ終了した場合、既存インスタンスへ処理を渡した可能性が高い。
            // この状態はウィンドウが増える不具合の原因になるため、記録に残しておく。
            // 起動処理を待たせないよう、確認は別スレッドで行う。
            var started = _process;
            if (started != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (started.WaitForExit(1500))
                            _log.Warn("起動した認識用ブラウザのプロセスが即座に終了しました。" +
                                      "既存のブラウザへ処理が渡された可能性があります" +
                                      "（動作中かどうかは認識ブリッジへの接続で判断します）");
                    }
                    catch
                    {
                        // 既に破棄されている場合は何もしない
                    }
                });
            }

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

    /// <summary>
    /// 前回の実行で取り残された認識用ブラウザを終了させる。
    /// VoiceDock がクラッシュすると Dispose が走らず、画面外のブラウザが
    /// マイクを掴んだまま残り続けるため、起動時に片付ける。
    ///
    /// PID は OS が使い回すため、PID だけで判断すると利用者が普段使っている
    /// ブラウザをタブごと巻き添えで終了させてしまう。記録した「起動時刻」と
    /// 一致することを確かめ、確実に自分が起動したプロセスだけを終了する。
    /// </summary>
    public void KillOrphanedBrowser()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            var text = File.ReadAllText(PidFile).Trim();
            File.Delete(PidFile);

            // 形式: "<pid>|<開始時刻のTicks>"
            var parts = text.Split('|');
            if (parts.Length != 2) return;
            if (!int.TryParse(parts[0], out int pid)) return;
            if (!long.TryParse(parts[1], out long startedTicks)) return;

            using var proc = Process.GetProcessById(pid);

            // 名前と開始時刻の両方が一致した場合だけ、自分が起動したものと判断する
            var name = proc.ProcessName;
            if (!name.Equals("msedge", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("chrome", StringComparison.OrdinalIgnoreCase))
                return;
            if (proc.StartTime.Ticks != startedTicks)
            {
                // PID が別のプロセスに再利用されている（利用者のブラウザ等）
                return;
            }

            proc.Kill(entireProcessTree: true);
            _log.Info("前回の実行で残っていた認識用ブラウザを終了しました");
        }
        catch
        {
            // 既に終了している場合は何もしない
        }
    }

    private static string PidFile => Path.Combine(AppPaths.Root, "browser.pid");

    private void SaveLaunchedPid(Process proc)
    {
        try
        {
            AppPaths.EnsureDirectories();
            // PID の使い回しを見分けるため、開始時刻も一緒に残す
            File.WriteAllText(PidFile, $"{proc.Id}|{proc.StartTime.Ticks}");
        }
        catch
        {
            // 記録できなくても動作に支障はない
        }
    }

    private static void ClearLaunchedPid()
    {
        try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { /* 無視 */ }
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
        KillCurrent();
        // 手元のハンドルが既に終了していても、プロファイルで動いている本体が残ることがある。
        // 終了時に片付けないと、画面外のウィンドウがマイクを掴んだまま残り続ける。
        KillProfileInstance();
        ClearLaunchedPid();
    }
}
