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
    private bool _initializing = true;

    public SettingsWindow(SettingsService settings,
        Func<HotkeySpec, bool> applyHotkey, Func<HotkeySpec, bool> applyUndoHotkey,
        Action openDictionary, Action openSnippets, Action openAppRules)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _settings = settings;
        _applyHotkey = applyHotkey;
        _applyUndoHotkey = applyUndoHotkey;
        _openDictionary = openDictionary;
        _openSnippets = openSnippets;
        _openAppRules = openAppRules;

        HotkeyBox.Text = settings.Current.Hotkey;
        UndoHotkeyBox.Text = settings.Current.UndoHotkey;
        StartupCheck.IsChecked = settings.Current.StartupEnabled;

        HotkeyModeCombo.Items.Add("押すたびに開始/停止（トグル）");
        HotkeyModeCombo.Items.Add("押している間だけ録音");
        HotkeyModeCombo.SelectedIndex = settings.Current.HotkeyMode == HotkeyMode.PushToTalk ? 1 : 0;

        NewlineCombo.Items.Add("Shift+Enter（チャットアプリ向け・推奨）");
        NewlineCombo.Items.Add("Enter（メモ帳・エディタ向け）");
        NewlineCombo.SelectedIndex = settings.Current.NewlineMode == NewlineMode.Enter ? 1 : 0;

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

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"VoiceDock v{version?.ToString(3) ?? "?"} — Web Speech API 音声入力ツール";

        _initializing = false;
    }

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
        ToastWindow.Show("認識用ブラウザの変更は、VoiceDock を再起動すると反映されます。");
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
        });
    }

    private void NewlineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var mode = NewlineCombo.SelectedIndex == 1 ? NewlineMode.Enter : NewlineMode.ShiftEnter;
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

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
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

    private void OpenSnippets_Click(object sender, RoutedEventArgs e) => _openSnippets();

    private void OpenAppRules_Click(object sender, RoutedEventArgs e) => _openAppRules();

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
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
            ToastWindow.Show($"スタートアップ登録の変更に失敗しました: {ex.Message}", ToastKind.Error);
        }
    }

    private void OpenDictionaryButton_Click(object sender, RoutedEventArgs e) => _openDictionary();
}
