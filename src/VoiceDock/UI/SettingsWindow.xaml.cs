using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 設定画面（ダークテーマ固定・縦リスト形式・即時反映）。
/// </summary>
public partial class SettingsWindow : Window
{
    private const string DefaultDeviceLabel = "既定のデバイス";

    private readonly SettingsService _settings;
    private readonly Func<HotkeySpec, bool> _applyHotkey;
    private readonly Action _openDictionary;
    private bool _initializing = true;

    public SettingsWindow(SettingsService settings, Func<HotkeySpec, bool> applyHotkey, Action openDictionary)
    {
        InitializeComponent();
        _settings = settings;
        _applyHotkey = applyHotkey;
        _openDictionary = openDictionary;

        HotkeyBox.Text = settings.Current.Hotkey;

        foreach (var size in ModelDownloader.ModelSizes)
            ModelCombo.Items.Add(size);
        ModelCombo.SelectedItem = settings.Current.ModelSize;
        if (ModelCombo.SelectedItem == null) ModelCombo.SelectedItem = "small";

        MicCombo.Items.Add(DefaultDeviceLabel);
        foreach (var (_, name) in AudioRecorder.GetDevices())
            MicCombo.Items.Add(name);
        MicCombo.SelectedItem = string.IsNullOrWhiteSpace(settings.Current.MicDeviceName)
            ? DefaultDeviceLabel
            : settings.Current.MicDeviceName;
        if (MicCombo.SelectedItem == null) MicCombo.SelectedIndex = 0;

        StartupCheck.IsChecked = settings.Current.StartupEnabled;

        _initializing = false;
    }

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

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ModelCombo.SelectedItem is not string size) return;
        _settings.Update(s => s.ModelSize = size);
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MicCombo.SelectedItem is not string name) return;
        _settings.Update(s => s.MicDeviceName = name == DefaultDeviceLabel ? null : name);
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
