using System.Windows;
using System.Windows.Threading;
using VoiceDock.Models;
using VoiceDock.Services;
using VoiceDock.UI;

namespace VoiceDock;

/// <summary>
/// アプリケーション本体。タスクトレイに常駐し、各サービス・画面を束ねる。
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private LogService? _log;
    private SettingsService? _settings;
    private DictionaryService? _dictionary;
    private SnippetService? _snippets;
    private UpdateService? _updater;
    private SpeechBridgeServer? _bridge;
    private BrowserLauncher? _browser;
    private HotkeyManager? _hotkey;
    private TrayIconController? _tray;
    private OverlayWindow? _overlay;
    private RecordingController? _controller;
    private System.Windows.Threading.DispatcherTimer? _watchdog;

    private DateTime _lastRelaunchUtc = DateTime.MinValue;

    /// <summary>認識用ブラウザの監視の間隔。</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);

    /// <summary>この時間つながらないままなら、認識用ブラウザが落ちたとみなして再起動する（一瞬の切断は 1 秒ほどで戻る）。</summary>
    private static readonly TimeSpan BrowserDownThreshold = TimeSpan.FromSeconds(15);

    /// <summary>再起動した後、つながるのを待つ時間（その間は重ねて再起動しない）。</summary>
    private static readonly TimeSpan RelaunchCooldown = TimeSpan.FromSeconds(30);

    private SettingsWindow? _settingsWindow;
    private DictionaryWindow? _dictionaryWindow;
    private SnippetWindow? _snippetWindow;
    private AppRulesWindow? _appRulesWindow;
    private LogWindow? _logWindow;
    private HistoryWindow? _historyWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private BackupService? _backup;
    private UpdateWindow? _updateWindow;

    /// <summary>起動時の確認で見つかった更新（トレイから開くまで保持する）。</summary>
    private UpdateInfo? _pendingUpdate;

    /// <summary>使っていないときに入れ替えるため、裏でダウンロードしておいた新しい版。</summary>
    private UpdateInfo? _preparedUpdate;
    private string? _preparedUpdateFile;
    private bool _preparingUpdate;

    /// <summary>使っていないかを確かめ、入れ替えるためのタイマー（ダウンロードが済んでから動かす）。</summary>
    private DispatcherTimer? _idleUpdateTimer;

    /// <summary>起動したまま何日も使う人のため、起動中も定期的に新しい版を確かめるタイマー。</summary>
    private DispatcherTimer? _periodicUpdateCheckTimer;

    private static readonly TimeSpan IdleUpdateCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PeriodicUpdateCheckInterval = TimeSpan.FromHours(6);

    /// <summary>予期しないエラーを知らせた最後の時刻（同じエラーが続くときに通知で埋めないため）。</summary>
    private DateTime _unexpectedErrorNotifiedUtc = DateTime.MinValue;

    /// <summary>再起動の上限に達したことを知らせたか（同じ通知を繰り返さないため）。</summary>
    private bool _gaveUpNotified;

    /// <summary>取り消しのホットキーを登録しておきたい設定だったか（設定が変わったときだけ登録し直すため）。</summary>
    private string _undoHotkeyApplied = "";

    /// <summary>
    /// 予期しないエラーでアプリごと落ちないようにする。
    /// 常駐アプリが黙って消えると、利用者は「ホットキーが効かない」としか分からず、IME がオフのまま残ったり、
    /// 認識用ブラウザが取り残されたりする。ログに残し、IME を戻し、1 度だけ知らせて動き続ける。
    /// </summary>
    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            ReportUnexpectedError(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error($"裏の処理で予期しないエラーが発生しました: {args.Exception}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // ここに来たら終了は避けられない。原因を残し、IME だけは戻しておく
            try { _log?.Error($"予期しないエラーで終了します: {args.ExceptionObject}"); } catch { /* 無視 */ }
            try { TextInjector.RestoreSuppressedIme(); } catch { /* 無視 */ }
        };
    }

    private void ReportUnexpectedError(Exception ex)
    {
        try { _log?.Error($"予期しないエラーが発生しました: {ex}"); } catch { /* 無視 */ }
        try { TextInjector.RestoreSuppressedIme(); } catch { /* 無視 */ }
        try
        {
            if (DateTime.UtcNow - _unexpectedErrorNotifiedUtc < TimeSpan.FromMinutes(1)) return;
            _unexpectedErrorNotifiedUtc = DateTime.UtcNow;
            ToastWindow.Show("予期しないエラーが発生しました。動作がおかしい場合は、トレイメニューの「トラブル解決 → ログ」をご確認ください。",
                ToastKind.Error, onClick: ShowLog, actionHint: "クリックするとログを開きます");
        }
        catch { /* 知らせられなくても動き続ける */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallExceptionHandlers();

        // 二重起動制御: 既に起動中なら既存インスタンスへ通知して終了する。
        // 更新直後の再起動では、旧バージョンがまだ終了しきっていないため待ってから判定する
        // （待たないと、更新のたびにアプリがトレイから消えてしまう）
        bool afterUpdate = e.Args.Contains(UpdateService.AfterUpdateArgument);
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.TryAcquire(afterUpdate ? UpdateService.AfterUpdateWait : TimeSpan.Zero))
        {
            Shutdown();
            return;
        }
        _instanceGuard.ActivationRequested += () =>
            Dispatcher.BeginInvoke(() =>
                ToastWindow.Show("VoiceDock は既に起動しています。タスクトレイのアイコンから操作できます。"));

        // 以前の版は %APPDATA%\VoiceDock に保存していた。exe の隣の data フォルダへ写してから読み込む
        int migrated = AppPaths.IsPortable ? AppPaths.MigrateFromLegacy() : 0;
        AppPaths.EnsureDirectories();

        _log = new LogService();
        _log.CleanupOldLogs();
        _log.Info("VoiceDock を起動しました");
        _log.Info($"保存先: {AppPaths.Root}" + (AppPaths.IsPortable ? "" : "（exe の場所に書き込めないため）"));
        if (migrated > 0)
            _log.Info($"以前の保存先（{AppPaths.LegacyRoot}）から {migrated} 個のファイルを写しました。以前の保存先はそのまま残しています");

        _settings = new SettingsService(_log);
        _settings.Load();

        WindowBehavior.Settings = _settings;

        // 認識テキストをファイルに残すかは設定に従う（既定は残さない）
        _log.PersistRecognitionText = _settings.Current.LogRecognitionText;
        _settings.Changed += s => _log!.PersistRecognitionText = s.LogRecognitionText;

        // 保存に失敗したことを黙っていると、変更が残ったと誤解されてしまう
        _settings.SaveFailed += reason => Dispatcher.BeginInvoke(() =>
            ToastWindow.Show($"設定を保存できませんでした。{reason}", ToastKind.Error));

        if (_settings.ResetBecauseUnreadable)
            ToastWindow.Show("設定ファイルが読み取れなかったため、初期設定で起動しました。", ToastKind.Warning);
        else if (_settings.RecoveredFromBackup)
            ToastWindow.Show("設定ファイルが壊れていたため、バックアップから復帰しました。", ToastKind.Warning);

        _dictionary = new DictionaryService(_log);
        _dictionary.Load();
        _dictionary.SaveFailed += reason => Dispatcher.BeginInvoke(() =>
            ToastWindow.Show($"辞書を保存できませんでした。{reason}", ToastKind.Error));

        _snippets = new SnippetService(_log);
        _snippets.Load();
        _snippets.SaveFailed += reason => Dispatcher.BeginInvoke(() =>
            ToastWindow.Show($"定型文を保存できませんでした。{reason}", ToastKind.Error));

        // 読み込めなかったことを伝えないと、消えたと思って作り直されてしまう。
        // この状態では上書き保存も見送るため、その旨もあわせて知らせる。
        if (_dictionary.LoadFailed)
            ToastWindow.Show("辞書ファイルを読み取れませんでした。中身を失わないよう、保存は行いません。", ToastKind.Error);
        else if (_dictionary.RecoveredFromBackup)
            ToastWindow.Show("辞書ファイルが壊れていたため、バックアップから復帰しました。", ToastKind.Warning);

        if (_snippets.LoadFailed)
            ToastWindow.Show("定型文ファイルを読み取れませんでした。中身を失わないよう、保存は行いません。", ToastKind.Error);
        else if (_snippets.RecoveredFromBackup)
            ToastWindow.Show("定型文ファイルが壊れていたため、バックアップから復帰しました。", ToastKind.Warning);

        _backup = new BackupService(_settings, _dictionary, _snippets, _log);

        _tray = new TrayIconController();
        _tray.SettingsRequested += ShowSettings;
        _tray.AppRulesRequested += ShowAppRules;
        _tray.OpenDataFolderRequested += OpenDataFolder;
        _tray.HelpRequested += OpenHelp;
        _tray.AboutRequested += ShowAbout;
        _tray.DictionaryRequested += ShowDictionary;
        _tray.SnippetRequested += ShowSnippets;
        _tray.LogRequested += ShowLog;
        _tray.RestartEngineRequested += RestartBrowser;
        _tray.DiagnosticsRequested += ShowDiagnostics;
        _tray.UpdateCheckRequested += () =>
        {
            // 起動時に見つけた更新があれば、通信し直さずそのまま案内画面を出す
            if (_pendingUpdate != null) ShowUpdateWindow(_pendingUpdate);
            else _ = CheckForUpdateAsync(manual: true);
        };
        _tray.ExitRequested += ExitApplication;

        // 前回の更新で残ったファイルを片付ける
        _updater = new UpdateService(_log, _settings);
        _updater.CleanupOldFiles();

        _overlay = new OverlayWindow { Position = _settings.Current.OverlayPosition };
        _settings.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            if (_overlay == null || _overlay.Position == s.OverlayPosition) return;
            // 音声入力中に変えた場合も、その場で新しい位置へ出し直す
            _overlay.HideOverlay();
            _overlay.Position = s.OverlayPosition;
            if (_controller is { IsListening: true }) _overlay.ShowOverlay();
        });

        // 認識ブリッジ（ローカルサーバー）を起動し、認識用ブラウザ(Edge)を裏で立ち上げる
        _bridge = new SpeechBridgeServer(_log);
        _bridge.Ready += () => Dispatcher.BeginInvoke(() =>
        {
            _log!.Info("認識エンジンの準備が完了しました");
            // 起動直後は認識できないため、準備が整ったことをトレイに反映する
            _tray?.SetState(TrayState.Idle);
        });
        // 選んだマイクが見つからないときは、既定のマイクで続けていることを知らせる
        _bridge.SelectedMicrophoneUnavailable += label => Dispatcher.BeginInvoke(() =>
        {
            _log!.Warn($"選んだマイク（{label}）が見つからないため、Windows の既定のマイクで認識します");
            ToastWindow.Show($"選んだマイク{(label.Length > 0 ? $"「{label}」" : "")}が見つからないため、Windows の既定のマイクで認識しています。",
                ToastKind.Warning);
        });
        // 使用中のマイクを記録し、途中で切り替わったら知らせる
        _bridge.MicrophoneChanged += (previous, current) => Dispatcher.BeginInvoke(() =>
        {
            _log!.Info($"使用中のマイク: {current}");
            if (previous != null)
                ToastWindow.Show($"マイクが「{current}」に切り替わりました。");
        });

        try
        {
            _bridge.Start();
            _browser = new BrowserLauncher(_log, _settings);
            _browser.KillOrphanedBrowser();
            // 起動してからつながるまでの間は、監視が「落ちた」と見なさないようにする
            // （起動の遅い PC では、起動途中のブラウザを監視が終了させてしまっていた）
            _lastRelaunchUtc = DateTime.UtcNow;
            if (!_browser.Launch(_bridge.IssuePageUrl))
            {
                _tray.SetState(TrayState.Error);
                ToastWindow.Show("認識用ブラウザ (Edge/Chrome) が見つからず、音声認識を利用できません。", ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"認識ブリッジの初期化に失敗しました: {ex.Message}");
            _tray.SetState(TrayState.Error);
            ToastWindow.Show($"認識エンジンを開始できませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }

        var processor = new TextProcessor(_settings, _dictionary, _snippets);
        _controller = new RecordingController(_log, _settings, processor, _bridge, _tray, _overlay);
        _controller.ListeningChanged += listening => _tray!.SetListening(listening);
        _tray.RecordToggleRequested += () => _controller!.ToggleFromMenu();
        // メニューを開いた時点で入力先は前面でなくなっているため、入力したウィンドウへ戻してから取り消す
        _tray.UndoRequested += () => _controller!.UndoLastInjection("トレイメニュー", reactivateTarget: true);
        _tray.CanStart = () => _bridge is { IsConnected: true };
        _tray.CanUndo = () => _controller is { CanUndo: true };
        _tray.HasHistory = () => _controller is { History.Count: > 0 };
        UpdateTrayHotkeys(_settings.Current);
        _tray.RegisterLastToDictionaryRequested += RegisterLastToDictionary;
        _tray.HistoryRequested += ShowHistory;
        _controller.FrequentUndoDetected += SuggestRegisterUndone;

        // 認識言語: トレイから選べるようにし、設定画面で変えた場合もメニューに反映する
        _tray.SetLanguage(_settings.Current.RecognitionLanguage);
        _tray.LanguageSelected += code =>
        {
            if (_settings.Current.RecognitionLanguage == code) return;
            _settings.Update(s => s.RecognitionLanguage = code);
            ToastWindow.Show($"認識言語を{RecognitionLanguages.LabelOf(code)}に切り替えました。");
        };
        _settings.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetLanguage(s.RecognitionLanguage);
            UpdateTrayHotkeys(s);
        });

        // ブラウザ監視: 認識用ブラウザが落ちていたら自動再起動する
        _watchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = WatchdogInterval,
        };
        _watchdog.Tick += (_, _) =>
        {
            if (_browser == null || _bridge == null) return;

            // 生きているかどうかは、プロセスの有無ではなく認識ブリッジへの接続で判断する。
            //
            // Chromium は同じプロファイルで起動されると既存インスタンスへ処理を渡して
            // すぐ終了する。プロセスの終了だけを見て再起動していたため、
            // 実際には動いているブラウザに新しいウィンドウを開き続け、
            // 認識用ウィンドウが際限なく増えてしまっていた。
            //
            // 切れた時間で判断する。以前は 20 秒ごとの確認で「2 回続けて切れていたら」としていたため、
            // ページが 1 秒でつなぎ直す一瞬の切断が 2 回の確認に重なると、動いているブラウザを再起動していた
            // （実機ログ 2026-11-05〜12）。また本当に落ちたときは、気づくまで最大 40 秒かかっていた。
            if (_bridge.DisconnectedFor < BrowserDownThreshold) return;
            if (_browser.IsRelaunching || DateTime.UtcNow - _lastRelaunchUtc < RelaunchCooldown) return;

            // 起動できない環境で再起動を繰り返さないよう、上限に達したら休む。
            // 監視そのものは止めない（しばらくして上限の期間が明けたら、また試す）
            if (_browser.GaveUp)
            {
                if (_gaveUpNotified) return;
                _gaveUpNotified = true;
                _tray!.SetState(TrayState.Error);
                ToastWindow.Show("認識用ブラウザを起動できないため、音声入力を利用できません。" +
                                 "設定で使用ブラウザをご確認いただくか、トレイメニューの「トラブル解決 → 認識エンジンを再起動」をお試しください。",
                                 ToastKind.Error);
                return;
            }
            _gaveUpNotified = false;
            _lastRelaunchUtc = DateTime.UtcNow;
            _browser.RelaunchInBackground();
        };
        _watchdog.Start();

        // グローバルホットキー登録（衝突時はトーストで警告）
        _hotkey = new HotkeyManager
        {
            PushToTalk = _settings.Current.HotkeyMode == HotkeyMode.PushToTalk,
        };
        _hotkey.HotkeyPressed += () =>
        {
            if (_hotkey!.PushToTalk) _controller!.BeginPushToTalk();
            else _controller!.Toggle();
        };
        _hotkey.HotkeyReleased += () => _controller!.EndPushToTalk();
        _hotkey.UndoPressed += () => _controller!.UndoLastInjection("ホットキー");

        if (HotkeySpec.TryParse(_settings.Current.Hotkey, out var spec))
        {
            if (!_hotkey.TryRegister(spec))
            {
                _log.Warn($"ホットキー {spec} は他のアプリと衝突しているため登録できませんでした");
                ToastWindow.Show($"ホットキー {spec} は他のアプリと衝突しているため登録できませんでした。設定画面から変更してください。", ToastKind.Warning);
            }
        }
        else
        {
            _log.Warn($"ホットキー設定 \"{_settings.Current.Hotkey}\" を解釈できませんでした");
        }

        // 取り消しホットキー。押しても無反応な理由が分かるよう、衝突時は通知する
        SyncUndoHotkey(_settings.Current, "設定画面から変更してください。");

        // 設定変更（ホットキーの動作、取り消しのオン/オフ）を即座にホットキー側へ反映する
        _settings.Changed += s =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_hotkey == null) return;
                _hotkey.PushToTalk = s.HotkeyMode == HotkeyMode.PushToTalk;
                SyncUndoHotkey(s, "設定画面から変更してください。");
            });

        // 初期化・復元で設定が丸ごと変わったときは、ホットキーやスタートアップ登録もその場で合わせ直す
        _settings.Replaced += () => Dispatcher.BeginInvoke(ApplySettingsToRuntime);

        // スタートアップ登録を設定に同期する
        try
        {
            StartupManager.SetEnabled(_settings.Current.StartupEnabled);
        }
        catch (Exception ex)
        {
            _log.Warn($"スタートアップ登録の同期に失敗しました: {ex.Message}");
        }

        // 初回起動ガイド。どこにいるのか（トレイ）と、最初にすることを伝える
        if (!_settings.Current.FirstRunDone)
        {
            _settings.Update(s => s.FirstRunDone = true);
            ToastWindow.Show($"VoiceDock へようこそ。文字を入れたい場所にカーソルを置き、{_settings.Current.Hotkey} を押して話すと入力されます。" +
                             "設定は、画面右下（タスクトレイ）のマイクのアイコンを右クリックして開けます。",
                ToastKind.Info, onClick: ShowSettings, actionHint: "クリックすると設定を開きます");
        }

        // 更新で再起動した直後は、更新できたことを知らせる（自動で入れ替えた場合、利用者は再起動に気づいていない）
        if (afterUpdate)
            ToastWindow.Show($"VoiceDock を v{UpdateService.CurrentVersion} に更新しました。");

        // 更新の確認。通信が起動をブロックしないよう非同期で行う
        if (_updater.ShouldCheckOnStartup())
            _ = CheckForUpdateAsync(manual: false);

        // 起動したまま使い続ける場合も、自動で入れ替える設定なら定期的に確かめる
        _periodicUpdateCheckTimer = new DispatcherTimer { Interval = PeriodicUpdateCheckInterval };
        _periodicUpdateCheckTimer.Tick += (_, _) =>
        {
            if (_settings?.Current is { AutoApplyUpdate: true } s && s.UpdateCheckMode != UpdateCheckMode.Manual
                && _preparedUpdate == null && !_preparingUpdate)
                _ = CheckForUpdateAsync(manual: false);
        };
        _periodicUpdateCheckTimer.Start();
    }

    /// <summary>
    /// 更新を確認し、新しい版があれば案内画面を出す。
    /// 手動実行時は「最新です」「確認できませんでした」も通知する。
    /// </summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updater == null) return;

        // 連打された場合は最初の 1 回だけ通す
        if (_updater.IsChecking)
        {
            if (manual) ToastWindow.Show("更新を確認しています…");
            return;
        }

        // 待たされていることが分かるようにする（手動実行時は無反応だと固まって見える）
        if (manual) ToastWindow.Show("更新を確認しています…");

        var info = await _updater.CheckForUpdateAsync(ignoreSkipped: manual);

        await Dispatcher.BeginInvoke(() =>
        {
            if (info == null)
            {
                // 確認に失敗したのに「最新です」と伝えると、古い版を使い続けてしまう
                if (manual && _updater.LastCheckError is { } error)
                {
                    ToastWindow.Show(error, ToastKind.Warning);
                    return;
                }
                if (manual)
                {
                    // 既に手で入れ替えた等で最新になっている場合、案内を残さない
                    _pendingUpdate = null;
                    _tray?.SetUpdateAvailable(false);
                    ToastWindow.Show($"お使いのバージョン {UpdateService.CurrentVersion} は最新です。");
                }
                return;
            }

            // 起動時の自動確認で見つかった場合も、更新画面を開いて知らせる。
            // トースト通知だけでは見落とされやすく、古い版を使い続けてしまうため。
            // 「あとで」で閉じても、トレイメニューの「更新を確認」から開き直せるよう保持しておく。
            if (!manual)
            {
                _pendingUpdate = info;
                _tray?.SetUpdateAvailable(true, info.Version.ToString());

                // 自動で入れ替える設定なら、画面は出さずに裏でダウンロードし、使っていないときに入れ替える
                if (_settings!.Current.AutoApplyUpdate && UpdateService.CanWriteToInstallDir(out _))
                {
                    _ = PrepareAutoUpdateAsync(info);
                    return;
                }

                // 話している最中に画面を出すと、入力先からフォーカスを奪って文字が入らなくなる。
                // 音声入力中は通知だけにし、トレイメニューのいちばん上からいつでも開けるようにしておく
                if (_controller is { IsListening: true })
                {
                    ToastWindow.Show($"新しい版 v{info.Version} があります。トレイメニューから更新できます。",
                        ToastKind.Info, onClick: () => ShowUpdateWindow(info), actionHint: "クリックすると更新の画面を開きます");
                    return;
                }
            }

            ShowUpdateWindow(info, activate: manual);
        });
    }

    /// <summary>
    /// 新しい版を裏でダウンロードし、使っていないときに入れ替えるよう待つ。
    /// ダウンロードに失敗したら、これまでどおり更新画面で知らせる。
    /// </summary>
    private async Task PrepareAutoUpdateAsync(UpdateInfo info)
    {
        if (_updater == null || _preparingUpdate) return;
        if (_preparedUpdate != null && _preparedUpdate.Version >= info.Version) return;

        _preparingUpdate = true;
        try
        {
            _log?.Info($"新しい版 {info.Version} を裏でダウンロードします（使っていないときに自動で入れ替えます）");
            var file = await _updater.DownloadAsync(info, progress: null);
            _preparedUpdate = info;
            _preparedUpdateFile = file;
            ToastWindow.Show($"新しい版 v{info.Version} を用意しました。使っていないときに自動で入れ替えます" +
                             "（音声入力中や VoiceDock の画面を開いている間は入れ替えません）。");

            _idleUpdateTimer ??= CreateIdleUpdateTimer();
            _idleUpdateTimer.Start();
        }
        catch (Exception ex)
        {
            _log?.Warn($"新しい版を裏でダウンロードできませんでした。更新画面で知らせます: {ex.Message}");
            ShowUpdateWindow(info);
        }
        finally
        {
            _preparingUpdate = false;
        }
    }

    private DispatcherTimer CreateIdleUpdateTimer()
    {
        var timer = new DispatcherTimer { Interval = IdleUpdateCheckInterval };
        timer.Tick += async (_, _) => await TryApplyPreparedUpdateAsync();
        return timer;
    }

    /// <summary>使っていなければ、用意しておいた新しい版へ入れ替えて再起動する。</summary>
    private async Task TryApplyPreparedUpdateAsync()
    {
        if (_updater == null || _controller == null || _settings == null) return;
        var info = _preparedUpdate;
        var file = _preparedUpdateFile;
        if (info == null || file == null || !System.IO.File.Exists(file))
        {
            // 用意したファイルが消えていた（一時フォルダの掃除など）。用意し直せるよう、覚えを消す
            // （残したままだと、定期的な確認が「用意済み」と見なして二度と走らない）
            _idleUpdateTimer?.Stop();
            _preparedUpdate = null;
            _preparedUpdateFile = null;
            return;
        }
        // 途中で設定をオフにした場合は、更新画面での確認に戻す
        if (!_settings.Current.AutoApplyUpdate)
        {
            _idleUpdateTimer?.Stop();
            return;
        }

        bool windowOpen = Windows.OfType<Window>().Any(w => w.IsVisible && w is not OverlayWindow && w is not ToastWindow);
        var reason = IdleUpdatePolicy.WhyNotNow(
            _controller.IsListening,
            DateTime.UtcNow - _controller.LastUsedUtc,
            UserIdle.SinceLastInput(),
            windowOpen);
        if (reason != null) return;

        _idleUpdateTimer?.Stop();
        _log?.Info($"使っていないため、新しい版 {info.Version} へ入れ替えます");
        bool applied = await Task.Run(() => _updater.ApplyUpdate(file));
        if (applied)
        {
            ExitApplication(force: true);
            return;
        }

        // 入れ替えに失敗した場合は元の版のまま動いている。更新画面から手動で更新できるようにする
        _preparedUpdate = null;
        _preparedUpdateFile = null;
        ToastWindow.Show("新しい版へ自動で入れ替えられませんでした。トレイメニューのいちばん上から更新してください。", ToastKind.Warning);
    }

    /// <param name="activate">
    /// 画面を前面に出してキー入力を受け取るか。自動の確認で見つけたときは false
    /// （作業中のアプリからフォーカスを奪わない）。
    /// </param>
    private void ShowUpdateWindow(UpdateInfo info, bool activate = true)
    {
        if (_updater == null) return;
        if (_updateWindow is { IsLoaded: true })
        {
            if (activate) _updateWindow.Activate();
            return;
        }
        var window = new UpdateWindow(_updater, info, _settings!, () => ExitApplication(force: true))
        {
            ShowActivated = activate,
        };
        window.Closed += (_, _) =>
        {
            // 「この版はスキップ」を選んだら、保留中の案内とトレイの印も取り下げる
            if (!window.Skipped) return;
            if (_pendingUpdate?.Version == info.Version)
            {
                _pendingUpdate = null;
                _tray?.SetUpdateAvailable(false);
            }
        };
        _updateWindow = window;
        _updateWindow.Show();
        if (activate) _updateWindow.Activate();
    }

    /// <summary>ホットキーをトレイメニューに表示する。</summary>
    private void UpdateTrayHotkeys(AppSettings s) =>
        _tray?.SetHotkeys(s.Hotkey, s.UndoEnabled ? s.UndoHotkey : null);

    /// <summary>
    /// 取り消しのホットキーの登録を、設定（オン/オフとキー）に合わせる。
    /// 設定が前回と同じなら何もしない（設定を変えるたびに登録し直して、同じ警告を繰り返さないため）。
    /// </summary>
    private void SyncUndoHotkey(AppSettings s, string advice)
    {
        if (_hotkey == null) return;
        var wanted = s.UndoEnabled ? s.UndoHotkey : "";
        if (wanted == _undoHotkeyApplied) return;
        _undoHotkeyApplied = wanted;

        if (wanted.Length == 0 || !HotkeySpec.TryParse(wanted, out var undoSpec))
        {
            // 取り消しをオフにしたら、キーを他のアプリへ返す
            _hotkey.UnregisterUndo();
            return;
        }
        if (_hotkey.TryRegisterUndo(undoSpec)) return;
        _log?.Warn($"取り消しホットキー {undoSpec} は他のアプリと衝突しているため登録できませんでした");
        ToastWindow.Show($"取り消しのホットキー {undoSpec} は他のアプリと衝突しているため使えません。{advice}", ToastKind.Warning);
    }

    /// <summary>設定・辞書・ログの保存先をエクスプローラーで開く。</summary>
    private void OpenDataFolder()
    {
        try
        {
            AppPaths.EnsureDirectories();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log?.Warn($"保存先のフォルダーを開けませんでした: {ex.Message}");
            ToastWindow.Show($"保存先を開けませんでした。場所: {AppPaths.Root}", ToastKind.Warning);
        }
    }

    /// <summary>使い方のページ（README）を既定のブラウザで開く。</summary>
    private void OpenHelp()
    {
        const string url = "https://github.com/Yu5rin/VoiceDock#readme";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log?.Warn($"使い方のページを開けませんでした: {ex.Message}");
            ToastWindow.Show($"使い方のページを開けませんでした。{url}", ToastKind.Warning);
        }
    }

    private void ShowAbout()
    {
        MessageDialog.Show(null, "VoiceDock について",
            $"VoiceDock v{UpdateService.CurrentVersion}\n\n" +
            "話した言葉を、いま使っているアプリの入力欄へそのまま入力する、Windows 用の音声入力ツールです。\n\n" +
            $"データの保存先:\n{AppPaths.Root}\n\n" +
            "ライセンス: MIT License\n" +
            "https://github.com/Yu5rin/VoiceDock",
            MessageDialogKind.Info, "閉じる");
    }

    /// <summary>
    /// 設定の初期化・バックアップからの復元のあと、ホットキー・スタートアップ登録・
    /// 認識用ブラウザをその場で新しい設定に合わせ直す。アプリの再起動を求めずに済むようにするため。
    /// </summary>
    private void ApplySettingsToRuntime()
    {
        if (_settings == null) return;
        var s = _settings.Current;

        if (_hotkey != null)
        {
            _hotkey.PushToTalk = s.HotkeyMode == HotkeyMode.PushToTalk;

            if (HotkeySpec.TryParse(s.Hotkey, out var spec) && !_hotkey.TryRegister(spec))
                ToastWindow.Show($"ホットキー {spec} は他のアプリと衝突しているため登録できませんでした。", ToastKind.Warning);

            // 丸ごと変わったので、前回と同じキーでも登録し直す
            _undoHotkeyApplied = "\0";
            SyncUndoHotkey(s, "");
        }

        try
        {
            StartupManager.SetEnabled(s.StartupEnabled);
        }
        catch (Exception ex)
        {
            _log?.Warn($"スタートアップ登録の同期に失敗しました: {ex.Message}");
        }

        // 使用ブラウザの指定も変わりうるため、認識用ブラウザを開き直す（画面を止めないよう裏で行う）
        if (_browser != null) RestartBrowser(announce: false);
    }

    /// <summary>設定画面からのホットキー変更。衝突時は元のホットキーへ復元し false を返す。</summary>
    private bool ApplyHotkey(HotkeySpec spec)
    {
        if (_hotkey == null) return false;
        if (_hotkey.TryRegister(spec))
        {
            _log?.Info($"ホットキーを {spec} に変更しました");
            return true;
        }

        _log?.Warn($"ホットキー {spec} は他のアプリと衝突しています");
        if (HotkeySpec.TryParse(_settings?.Current.Hotkey, out var previous))
            _hotkey.TryRegister(previous);
        return false;
    }

    /// <summary>設定画面からの取り消しホットキー変更。衝突時は元のキーへ復元し false を返す。</summary>
    private bool ApplyUndoHotkey(HotkeySpec spec)
    {
        if (_hotkey == null) return false;
        if (_hotkey.TryRegisterUndo(spec))
        {
            _log?.Info($"取り消しホットキーを {spec} に変更しました");
            return true;
        }

        _log?.Warn($"取り消しホットキー {spec} は他のアプリと衝突しています");
        if (HotkeySpec.TryParse(_settings?.Current.UndoHotkey, out var previous))
            _hotkey.TryRegisterUndo(previous);
        return false;
    }

    private void ShowSettings()
    {
        if (_settings == null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplyHotkey, ApplyUndoHotkey,
            ShowDictionary, ShowSnippets, ShowAppRules, () => _ = CheckForUpdateAsync(manual: true),
            RestartBrowser, () => _bridge?.CurrentMicrophone, _backup!, RestoreBackup, _bridge,
            SetHotkeyCapture, OpenDataFolder, ReopenSettings);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>設定を初期化・復元した後、新しい内容で設定画面を開き直す。</summary>
    private void ReopenSettings()
    {
        _settingsWindow = null;
        Dispatcher.BeginInvoke(ShowSettings, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 設定画面でホットキーの入力を待っている間、登録しているホットキーを止める。
    /// 止めないと、いまのホットキーを押し直したときに音声入力が始まってしまう。
    /// </summary>
    private void SetHotkeyCapture(bool capturing)
    {
        if (_hotkey == null || _settings == null) return;
        if (capturing)
        {
            _hotkey.Unregister();
            _hotkey.UnregisterUndo();
            return;
        }
        var s = _settings.Current;
        if (HotkeySpec.TryParse(s.Hotkey, out var spec)) _hotkey.TryRegister(spec);
        if (s.UndoEnabled && HotkeySpec.TryParse(s.UndoHotkey, out var undoSpec)) _hotkey.TryRegisterUndo(undoSpec);
        _undoHotkeyApplied = s.UndoEnabled ? s.UndoHotkey : "";
    }

    /// <summary>
    /// 認識用ブラウザを起動し直す。使用ブラウザの設定を変えたときや、
    /// 再起動を諦めて止まってしまった状態から復帰させるときに使う。
    /// </summary>
    private void RestartBrowser() => RestartBrowser(announce: true);

    private async void RestartBrowser(bool announce)
    {
        if (_browser == null)
        {
            ToastWindow.Show("認識エンジンが起動していないため、VoiceDock を再起動してください。", ToastKind.Error);
            return;
        }

        // 古いブラウザが終わるのを待つため、画面を止めないよう別スレッドで行う。
        // その間に監視が重ねて再起動しないよう、再起動した時刻を先に記録する
        _lastRelaunchUtc = DateTime.UtcNow;
        var browser = _browser;
        if (announce) ToastWindow.Show("認識エンジンを再起動しています…");
        bool restarted;
        try
        {
            restarted = await Task.Run(() => browser.Restart());
        }
        catch (Exception ex)
        {
            _log?.Error($"認識用ブラウザの再起動に失敗しました: {ex}");
            restarted = false;
        }
        _lastRelaunchUtc = DateTime.UtcNow;
        if (restarted)
        {
            _gaveUpNotified = false;
            _tray?.SetState(_controller is { IsListening: true } ? TrayState.Recording : TrayState.Idle);
            if (announce) ToastWindow.Show($"認識エンジンを再起動しました（{_browser.LaunchedBrowserName}）。");
        }
        else
        {
            _tray?.SetState(TrayState.Error);
            ToastWindow.Show("認識用ブラウザを起動できませんでした。Microsoft Edge か Google Chrome がインストールされているかご確認ください。",
                ToastKind.Error);
        }
    }

    /// <summary>
    /// バックアップの内容で設定・辞書・定型文を置き換える。
    /// 辞書などの画面が開いていると、古い内容のまま上書き保存されてしまうため、先に閉じる。
    /// </summary>
    private bool RestoreBackup(BackupContents contents)
    {
        if (!TryCloseWindow(_dictionaryWindow) || !TryCloseWindow(_snippetWindow) || !TryCloseWindow(_appRulesWindow))
        {
            ToastWindow.Show("辞書・定型文などの画面を閉じてから、もう一度復元してください。", ToastKind.Warning);
            return false;
        }
        try
        {
            _backup!.Apply(contents);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error($"バックアップからの復元に失敗しました: {ex}");
            ToastWindow.Show($"復元できませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
            return false;
        }
    }

    /// <summary>ウィンドウを閉じる。利用者が閉じるのを取りやめた場合は false。</summary>
    private static bool TryCloseWindow(Window? window)
    {
        if (window is not { IsLoaded: true }) return true;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        return closed;
    }

    private void ShowSnippets()
    {
        if (_snippets == null) return;
        if (_snippetWindow is { IsLoaded: true })
        {
            _snippetWindow.Activate();
            return;
        }
        _snippetWindow = new SnippetWindow(_snippets, _log!);
        _snippetWindow.Show();
        _snippetWindow.Activate();
    }

    private void ShowAppRules()
    {
        if (_settings == null) return;
        if (_appRulesWindow is { IsLoaded: true })
        {
            _appRulesWindow.Activate();
            return;
        }
        _appRulesWindow = new AppRulesWindow(_settings, _controller?.SeenApps ?? Array.Empty<string>());
        _appRulesWindow.Show();
        _appRulesWindow.Activate();
    }

    private void ShowDictionary() => ShowDictionary(prefill: null);

    /// <param name="prefill">読みの欄に入れておく文字（直前の入力から登録する場合）。</param>
    private void ShowDictionary(string? prefill)
    {
        if (_dictionary == null) return;
        if (_dictionaryWindow is not { IsLoaded: true })
        {
            _dictionaryWindow = new DictionaryWindow(_dictionary, _log!);
            _dictionaryWindow.Show();
        }
        if (prefill != null) _dictionaryWindow.PrefillWrong(prefill);
        _dictionaryWindow.Activate();
    }

    /// <summary>
    /// 直前に認識した文章を読みの欄に入れた状態で、辞書管理を開く。
    /// 誤認識に気づいたときに、その場で辞書へ登録できるようにするため。
    /// </summary>
    private void RegisterLastToDictionary()
    {
        var last = _controller?.History.FirstOrDefault();
        if (last == null)
        {
            ToastWindow.Show("まだ音声入力の履歴がありません。音声入力をしてから使ってください。");
            return;
        }
        ShowDictionary(prefill: last.Text);
    }

    /// <summary>
    /// 同じ文章を何度も取り消したとき、誤認識しやすい語とみなして辞書への登録を勧める。
    /// 通知をクリックすると、その文章を読みの欄に入れた状態で辞書管理を開く。
    /// </summary>
    private void SuggestRegisterUndone(UndoneText undone)
    {
        var shown = undone.Text.Replace("\r", "").Replace("\n", " ");
        if (shown.Length > 30) shown = shown[..30] + "…";
        ToastWindow.Show(
            $"「{shown}」を {undone.Count} 回取り消しました。誤認識しやすい語なら、辞書に登録すると次から正しく入力されます。",
            ToastKind.Info,
            onClick: () => ShowDictionary(prefill: undone.Text),
            actionHint: "クリックすると辞書に登録できます");
    }

    private void ShowHistory()
    {
        if (_controller == null) return;
        if (_historyWindow is { IsLoaded: true })
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_controller, text => ShowDictionary(prefill: text));
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    private void ShowDiagnostics()
    {
        if (_settings == null || _controller == null) return;
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }
        _diagnosticsWindow = new DiagnosticsWindow(_settings, _bridge, _browser, _hotkey, _controller, ShowLog);
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }

    private void ShowLog()
    {
        if (_log == null) return;
        if (_logWindow is { IsLoaded: true })
        {
            _logWindow.Activate();
            return;
        }
        _logWindow = new LogWindow(_log);
        _logWindow.Show();
        _logWindow.Activate();
    }

    private void ExitApplication() => ExitApplication(force: false);

    /// <param name="force">
    /// 更新で入れ替えた直後は true（もう後戻りできないため、必ず終了する）。
    /// トレイからの終了では、保存していない入力がある画面で「キャンセル」を選んだら終了を取りやめる。
    /// </param>
    private void ExitApplication(bool force)
    {
        if (!force)
        {
            // Shutdown は、画面の「閉じるのをやめる」を無視して終了してしまう。
            // 先に 1 つずつ閉じて、取りやめられたら終了しない
            foreach (var window in new Window?[] { _dictionaryWindow, _snippetWindow, _appRulesWindow, _settingsWindow })
            {
                if (!TryCloseWindow(window)) return;
            }
        }
        _log?.Info("VoiceDock を終了します");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _idleUpdateTimer?.Stop();
        _periodicUpdateCheckTimer?.Stop();
        _watchdog?.Stop();
        _controller?.Dispose();
        // 辞書の使用回数はまとめて保存しているため、終了前に書き出す
        _dictionary?.Flush();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _browser?.Dispose();
        _bridge?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
