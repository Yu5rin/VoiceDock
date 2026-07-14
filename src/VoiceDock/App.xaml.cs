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
    private SpeechBridgeServer? _bridge;
    private BrowserLauncher? _browser;
    private HotkeyManager? _hotkey;
    private TrayIconController? _tray;
    private OverlayWindow? _overlay;
    private RecordingController? _controller;

    private SettingsWindow? _settingsWindow;
    private DictionaryWindow? _dictionaryWindow;
    private LogWindow? _logWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動制御: 既に起動中なら既存インスタンスへ通知して終了する
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.TryAcquire())
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

        _dictionary = new DictionaryService(_log);
        _dictionary.Load();

        _tray = new TrayIconController();
        _tray.SettingsRequested += ShowSettings;
        _tray.DictionaryRequested += ShowDictionary;
        _tray.LogRequested += ShowLog;
        _tray.ExitRequested += ExitApplication;

        _overlay = new OverlayWindow();

        // 認識ブリッジ（ローカルサーバー）を起動し、認識用ブラウザ(Edge)を裏で立ち上げる
        _bridge = new SpeechBridgeServer(_log);
        _bridge.Ready += () => _log.Info("認識エンジンの準備が完了しました");
        try
        {
            _bridge.Start();
            _browser = new BrowserLauncher(_log);
            if (!_browser.Launch(_bridge.PageUrl))
            {
                _tray.SetState(TrayState.Error);
                ToastWindow.Show("認識用ブラウザ (Edge/Chrome) が見つからず、音声認識を利用できません。", ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"認識ブリッジの初期化に失敗しました: {ex.Message}");
            _tray.SetState(TrayState.Error);
            ToastWindow.Show($"認識エンジンの初期化に失敗しました: {ex.Message}", ToastKind.Error);
        }

        _controller = new RecordingController(_log, _dictionary, _bridge, _tray, _overlay);

        // グローバルホットキー登録（衝突時はトーストで警告）
        _hotkey = new HotkeyManager();
        _hotkey.HotkeyPressed += () => _controller.Toggle();
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

        // スタートアップ登録を設定に同期する
        try
        {
            StartupManager.SetEnabled(_settings.Current.StartupEnabled);
        }
        catch (Exception ex)
        {
            _log.Warn($"スタートアップ登録の同期に失敗しました: {ex.Message}");
        }
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

    private void ShowSettings()
    {
        if (_settings == null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplyHotkey, ShowDictionary);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ShowDictionary()
    {
        if (_dictionary == null) return;
        if (_dictionaryWindow is { IsLoaded: true })
        {
            _dictionaryWindow.Activate();
            return;
        }
        _dictionaryWindow = new DictionaryWindow(_dictionary);
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
        _controller?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _browser?.Dispose();
        _bridge?.Dispose();
        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}
