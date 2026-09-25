using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceDock.Models;
using VoiceDock.Services;
using InputMethod = VoiceDock.Models.InputMethod;

namespace VoiceDock.UI;

/// <summary>
/// 設定画面（ダークテーマ固定・縦リスト形式・即時反映）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Func<HotkeySpec, bool> _applyHotkey;
    private readonly Func<HotkeySpec, bool> _applyUndoHotkey;
    private readonly Action _openDictionary;
    private readonly Action _openSnippets;
    private readonly Action _openAppRules;
    private readonly Action _checkUpdate;
    private readonly Action _restartBrowser;
    private readonly BackupService _backup;
    private readonly Func<BackupContents, bool> _restoreBackup;
    private bool _initializing = true;

    public SettingsWindow(SettingsService settings,
        Func<HotkeySpec, bool> applyHotkey, Func<HotkeySpec, bool> applyUndoHotkey,
        Action openDictionary, Action openSnippets, Action openAppRules, Action checkUpdate,
        Action restartBrowser, Func<string?> getMicrophone,
        BackupService backup, Func<BackupContents, bool> restoreBackup)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _settings = settings;
        _applyHotkey = applyHotkey;
        _applyUndoHotkey = applyUndoHotkey;
        _openDictionary = openDictionary;
        _openSnippets = openSnippets;
        _openAppRules = openAppRules;
        _checkUpdate = checkUpdate;
        _restartBrowser = restartBrowser;
        _backup = backup;
        _restoreBackup = restoreBackup;

        // マイクは選べない（Web Speech API に指定手段が無い）ため、
        // 今どれが使われているかだけを見えるようにする
        var mic = getMicrophone();
        MicrophoneText.Text = mic is { Length: > 0 }
            ? $"使用中のマイク: {mic}"
            : "使用中のマイク: 音声入力を一度行うと表示されます";

        HotkeyBox.Text = settings.Current.Hotkey;
        UndoHotkeyBox.Text = settings.Current.UndoHotkey;
        StartupCheck.IsChecked = settings.Current.StartupEnabled;

        HotkeyModeCombo.Items.Add("押すたびに開始/停止（トグル）");
        HotkeyModeCombo.Items.Add("押している間だけ音声入力（押しっぱなし）");
        HotkeyModeCombo.SelectedIndex = settings.Current.HotkeyMode == HotkeyMode.PushToTalk ? 1 : 0;

        NewlineCombo.Items.Add("Shift+Enter（推奨・大半のアプリで改行）");
        NewlineCombo.Items.Add("Enter（Shift+Enter が効かないアプリ向け）");
        NewlineCombo.Items.Add("Alt+Enter（Excel のセル内改行）");
        NewlineCombo.SelectedIndex = settings.Current.NewlineMode switch
        {
            NewlineMode.Enter => 1,
            NewlineMode.AltEnter => 2,
            _ => 0,
        };

        foreach (var (_, label) in RecognitionLanguages.All)
            LanguageCombo.Items.Add(label);
        LanguageCombo.SelectedIndex = Math.Max(0,
            RecognitionLanguages.All.ToList().FindIndex(l => l.Code == settings.Current.RecognitionLanguage));

        BrowserCombo.Items.Add("既定のブラウザに合わせる");
        BrowserCombo.Items.Add("常に Microsoft Edge");
        BrowserCombo.Items.Add("常に Google Chrome");
        BrowserCombo.SelectedIndex = settings.Current.Browser switch
        {
            BrowserChoice.Edge => 1,
            BrowserChoice.Chrome => 2,
            _ => 0,
        };

        InputMethodCombo.Items.Add("直接キー入力（推奨）");
        InputMethodCombo.Items.Add("クリップボード貼り付け");
        InputMethodCombo.SelectedIndex = settings.Current.InputMethod == InputMethod.Clipboard ? 1 : 0;

        RemoveSpacesCheck.IsChecked = settings.Current.RemoveSpaces;
        AutoPeriodCheck.IsChecked = settings.Current.AutoPeriod;
        VoiceCommandsCheck.IsChecked = settings.Current.VoiceCommandsEnabled;
        SnippetsCheck.IsChecked = settings.Current.SnippetsEnabled;
        UndoCheck.IsChecked = settings.Current.UndoEnabled;
        SoundCheck.IsChecked = settings.Current.SoundFeedback;
        LocalRecognitionCheck.IsChecked = settings.Current.PreferLocalRecognition;
        LogRecognitionCheck.IsChecked = settings.Current.LogRecognitionText;

        UpdateModeCombo.Items.Add("起動のたびに確認する（推奨）");
        UpdateModeCombo.Items.Add("起動時に確認する（1 日 1 回まで）");
        UpdateModeCombo.Items.Add("自動では確認しない（手動のみ）");
        UpdateModeCombo.SelectedIndex = settings.Current.UpdateCheckMode switch
        {
            UpdateCheckMode.DailyOnStartup => 1,
            UpdateCheckMode.Manual => 2,
            _ => 0,
        };
        // どこへ通信するのかが利用者から見えるようにする
        UpdateUrlText.Text = $"更新の確認先: {settings.Current.UpdateApiUrl}\n" +
                             "この確認と、更新ファイルのダウンロード以外に、外部との通信は行いません。";

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"VoiceDock v{version?.ToString(3) ?? "?"} — Web Speech API 音声入力ツール";

        _initializing = false;

        // トレイメニューから認識言語を切り替えた場合も、開いている画面の表示を合わせる
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings s) => Dispatcher.BeginInvoke(() =>
    {
        int index = RecognitionLanguages.All.ToList().FindIndex(l => l.Code == s.RecognitionLanguage);
        if (index < 0 || LanguageCombo.SelectedIndex == index) return;
        _initializing = true;
        LanguageCombo.SelectedIndex = index;
        _initializing = false;
    });

    private void BrowserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var choice = BrowserCombo.SelectedIndex switch
        {
            1 => BrowserChoice.Edge,
            2 => BrowserChoice.Chrome,
            _ => BrowserChoice.Auto,
        };
        _settings.Update(s => s.Browser = choice);
        // アプリを再起動させずにその場で切り替える
        _restartBrowser();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedIndex < 0) return;
        var code = RecognitionLanguages.All[LanguageCombo.SelectedIndex].Code;
        _settings.Update(s => s.RecognitionLanguage = code);
    }

    private void InputMethodCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.Update(s => s.InputMethod =
            InputMethodCombo.SelectedIndex == 1 ? InputMethod.Clipboard : InputMethod.SendInput);
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.Update(s =>
        {
            s.RemoveSpaces = RemoveSpacesCheck.IsChecked == true;
            s.AutoPeriod = AutoPeriodCheck.IsChecked == true;
            s.VoiceCommandsEnabled = VoiceCommandsCheck.IsChecked == true;
            s.SnippetsEnabled = SnippetsCheck.IsChecked == true;
            s.UndoEnabled = UndoCheck.IsChecked == true;
            s.SoundFeedback = SoundCheck.IsChecked == true;
            s.PreferLocalRecognition = LocalRecognitionCheck.IsChecked == true;
            s.LogRecognitionText = LogRecognitionCheck.IsChecked == true;
        });
    }

    private void NewlineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var mode = NewlineCombo.SelectedIndex switch
        {
            1 => NewlineMode.Enter,
            2 => NewlineMode.AltEnter,
            _ => NewlineMode.ShiftEnter,
        };
        _settings.Update(s => s.NewlineMode = mode);
    }

    private void HotkeyModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var mode = HotkeyModeCombo.SelectedIndex == 1 ? HotkeyMode.PushToTalk : HotkeyMode.Toggle;
        _settings.Update(s => s.HotkeyMode = mode);
    }

    private void UndoHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = ResolveKey(e);
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var spec = new HotkeySpec(Keyboard.Modifiers, key);
        if (spec.Modifiers == ModifierKeys.None)
        {
            UndoHotkeyWarning.Text = "修飾キー（Ctrl / Alt / Shift）との組み合わせを指定してください。";
            UndoHotkeyWarning.Visibility = Visibility.Visible;
            return;
        }

        if (_applyUndoHotkey(spec))
        {
            UndoHotkeyBox.Text = spec.ToString();
            UndoHotkeyWarning.Visibility = Visibility.Collapsed;
            _settings.Update(s => s.UndoHotkey = spec.ToString());
        }
        else
        {
            UndoHotkeyWarning.Text = $"{spec} は他のアプリと衝突しているため登録できませんでした。別のキーを指定してください。";
            UndoHotkeyWarning.Visibility = Visibility.Visible;
        }
    }

    private void UpdateModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var mode = UpdateModeCombo.SelectedIndex switch
        {
            1 => UpdateCheckMode.DailyOnStartup,
            2 => UpdateCheckMode.Manual,
            _ => UpdateCheckMode.EveryStartup,
        };
        _settings.Update(s => s.UpdateCheckMode = mode);
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => _checkUpdate();

    private void SaveBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "バックアップを保存",
            Filter = "VoiceDock バックアップ (*.json)|*.json",
            FileName = $"voicedock-backup-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _backup.Save(dialog.FileName);
            ToastWindow.Show("バックアップを保存しました。辞書や定型文の内容が含まれるため、取り扱いに注意してください。", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"バックアップを保存できませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "バックアップから復元",
            Filter = "VoiceDock バックアップ (*.json)|*.json|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        BackupContents contents;
        try
        {
            contents = BackupService.Load(dialog.FileName);
        }
        catch (System.IO.InvalidDataException ex)
        {
            ToastWindow.Show(ex.Message, ToastKind.Error);
            return;
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"バックアップを読み込めませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
            return;
        }

        // すべてを置き換えるため取り消しがきかない。何が入っているかを見せてから確認する
        var created = contents.CreatedAt is { } t ? t.ToString("yyyy/MM/dd HH:mm") : "不明";
        var answer = MessageBox.Show(this,
            "このバックアップで、今の設定・辞書・定型文をすべて置き換えます。\n\n" +
            $"作成日時: {created}\n" +
            $"作成したバージョン: {contents.AppVersion ?? "不明"}\n" +
            $"辞書: {contents.Dictionary.Count} 件　定型文: {contents.Snippets.Count} 件\n\n" +
            "この操作は取り消せません。続けますか？",
            "バックアップから復元", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        if (!_restoreBackup(contents)) return;

        ToastWindow.Show($"バックアップから復元しました（辞書 {contents.Dictionary.Count} 件・定型文 {contents.Snippets.Count} 件）。");
        // この画面は復元前の設定を表示しているため、閉じて開き直してもらう
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "すべての設定を初期状態に戻します。辞書と定型文は削除されません。\n続けますか？",
            "設定の初期化", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        _settings.ResetToDefaults();
        ToastWindow.Show("設定を初期状態に戻しました。");
        Close();
    }

    private void OpenSnippets_Click(object sender, RoutedEventArgs e) => _openSnippets();

    private void OpenAppRules_Click(object sender, RoutedEventArgs e) => _openAppRules();

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = ResolveKey(e);
        // 修飾キー単独は確定しない
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var spec = new HotkeySpec(Keyboard.Modifiers, key);
        if (spec.Modifiers == ModifierKeys.None)
        {
            ShowHotkeyWarning("修飾キー（Ctrl / Alt / Shift）との組み合わせを指定してください。");
            return;
        }

        if (_applyHotkey(spec))
        {
            HotkeyBox.Text = spec.ToString();
            HotkeyWarning.Visibility = Visibility.Collapsed;
            _settings.Update(s => s.Hotkey = spec.ToString());
        }
        else
        {
            // 他アプリとの衝突。元のホットキーを復元登録済み（App 側）なので表示は変えない
            ShowHotkeyWarning($"{spec} は他のアプリと衝突しているため登録できませんでした。別のキーを指定してください。");
        }
    }

    /// <summary>
    /// 押されたキーを取り出す。Alt との組み合わせは SystemKey、
    /// IME がオンのときは ImeProcessedKey に入るため、そのまま e.Key を見ると
    /// 「Ctrl+ImeProcessed」のような意味のない指定になってしまう。
    /// </summary>
    private static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private void ShowHotkeyWarning(string message)
    {
        HotkeyWarning.Text = message;
        HotkeyWarning.Visibility = Visibility.Visible;
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool enabled = StartupCheck.IsChecked == true;
        _settings.Update(s => s.StartupEnabled = enabled);
        try
        {
            StartupManager.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"スタートアップ登録を変更できませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void OpenDictionaryButton_Click(object sender, RoutedEventArgs e) => _openDictionary();
}
