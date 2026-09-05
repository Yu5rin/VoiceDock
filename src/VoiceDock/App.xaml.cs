using System.Windows;
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

    private SettingsWindow? _settingsWindow;
    private DictionaryWindow? _dictionaryWindow;
    private SnippetWindow? _snippetWindow;
    private AppRulesWindow? _appRulesWindow;
    private LogWindow? _logWindow;
    private UpdateWindow? _updateWindow;

    /// <summary>起動時の確認で見つかった更新（トレイから開くまで保持する）。</summary>
    private UpdateInfo? _pendingUpdate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        AppPaths.EnsureDirectories();

        _log = new LogService();
        _log.CleanupOldLogs();
        _log.Info("VoiceDock を起動しました");

        _settings = new SettingsService(_log);
        _settings.Load();

        // 認識テキストをファイルに残すかは設定に従う（既定は残さない）
        _log.PersistRecognitionText = _settings.Current.LogRecognitionText;
        _settings.Changed += s => _log!.PersistRecognitionText = s.LogRecognitionText;

        if (_settings.RecoveredFromBackup)
            ToastWindow.Show("設定ファイルが読み取れなかったため、初期設定で起動しました。", ToastKind.Warning);

        _dictionary = new DictionaryService(_log);
        _dictionary.Load();

        _snippets = new SnippetService(_log);
        _snippets.Load();

        _tray = new TrayIconController();
        _tray.SettingsRequested += ShowSettings;
        _tray.DictionaryRequested += ShowDictionary;
        _tray.SnippetRequested += ShowSnippets;
        _tray.LogRequested += ShowLog;
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

        _overlay = new OverlayWindow();

        // 認識ブリッジ（ローカルサーバー）を起動し、認識用ブラウザ(Edge)を裏で立ち上げる
        _bridge = new SpeechBridgeServer(_log);
        _bridge.Ready += () => Dispatcher.BeginInvoke(() =>
        {
            _log!.Info("認識エンジンの準備が完了しました");
            // 起動直後は認識できないため、準備が整ったことをトレイに反映する
            _tray?.SetState(TrayState.Idle);
        });
        try
        {
            _bridge.Start();
            _browser = new BrowserLauncher(_log, _settings);
            _browser.KillOrphanedBrowser();
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
        _tray.UndoRequested += () => _controller!.UndoLastInjection("トレイメニュー");

        // ブラウザ監視: 認識用ブラウザが落ちていたら自動再起動する
        _watchdog = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        _watchdog.Tick += (_, _) =>
        {
            if (_browser is not { IsRunning: false }) return;

            // 起動できない環境で無限に再試行しないよう、上限に達したら監視を止める
            if (_browser.GaveUp)
            {
                _watchdog!.Stop();
                _tray!.SetState(TrayState.Error);
                ToastWindow.Show("認識用ブラウザを起動できないため、音声入力を利用できません。設定から使用ブラウザをご確認ください。", ToastKind.Error);
                return;
            }
            _browser.Relaunch();
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
        if (_settings.Current.UndoEnabled && HotkeySpec.TryParse(_settings.Current.UndoHotkey, out var undoSpec))
        {
            if (!_hotkey.TryRegisterUndo(undoSpec))
            {
                _log.Warn($"取り消しホットキー {undoSpec} は他のアプリと衝突しているため登録できませんでした");
                ToastWindow.Show($"取り消しのホットキー {undoSpec} は他のアプリと衝突しているため使えません。設定画面から変更してください。", ToastKind.Warning);
            }
        }

        // 設定変更（操作方式）を即座にホットキー側へ反映する
        _settings.Changed += s =>
            Dispatcher.BeginInvoke(() =>
            {
                if (_hotkey != null) _hotkey.PushToTalk = s.HotkeyMode == HotkeyMode.PushToTalk;
            });

        // スタートアップ登録を設定に同期する
        try
        {
            StartupManager.SetEnabled(_settings.Current.StartupEnabled);
        }
        catch (Exception ex)
        {
            _log.Warn($"スタートアップ登録の同期に失敗しました: {ex.Message}");
        }

        // 初回起動ガイド
        if (!_settings.Current.FirstRunDone)
        {
            _settings.Update(s => s.FirstRunDone = true);
            ToastWindow.Show($"VoiceDock へようこそ。テキスト入力欄にカーソルを置き、{_settings.Current.Hotkey} を押して話すと文字が入力されます。");
        }

        // 更新の確認。通信が起動をブロックしないよう非同期で行う
        if (_updater.ShouldCheckOnStartup())
            _ = CheckForUpdateAsync(manual: false);
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
                if (manual)
                    ToastWindow.Show($"お使いのバージョン {UpdateService.CurrentVersion} は最新です。");
                return;
            }

            // 起動時の自動確認でウィンドウを前面に出すと、作業中に割り込んでしまう。
            // 自動のときは通知だけにとどめ、開くかどうかは利用者に委ねる。
            if (!manual)
            {
                _pendingUpdate = info;
                _tray?.SetUpdateAvailable(true);
                ToastWindow.Show($"新しいバージョン {info.Version} が利用できます。トレイメニューの「更新を確認」から更新できます。");
                return;
            }

            ShowUpdateWindow(info);
        });
    }

    private void ShowUpdateWindow(UpdateInfo info)
    {
        if (_updater == null) return;
        if (_updateWindow is { IsLoaded: true })
        {
            _updateWindow.Activate();
            return;
        }
        _updateWindow = new UpdateWindow(_updater, info, _settings!, ExitApplication);
        _updateWindow.Show();
        _updateWindow.Activate();
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
            ShowDictionary, ShowSnippets, ShowAppRules, () => _ = CheckForUpdateAsync(manual: true));
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowSnippets()
    {
        if (_snippets == null) return;
        if (_snippetWindow is { IsLoaded: true })
        {
            _snippetWindow.Activate();
            return;
        }
        _snippetWindow = new SnippetWindow(_snippets);
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

    private void ShowDictionary()
    {
        if (_dictionary == null) return;
        if (_dictionaryWindow is { IsLoaded: true })
        {
            _dictionaryWindow.Activate();
            return;
        }
        _dictionaryWindow = new DictionaryWindow(_dictionary, _log!);
        _dictionaryWindow.Show();
        _dictionaryWindow.Activate();
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

    private void ExitApplication()
    {
        _log?.Info("VoiceDock を終了します");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Stop();
        _controller?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _browser?.Dispose();
        _bridge?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
