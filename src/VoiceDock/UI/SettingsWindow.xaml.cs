using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceDock.Models;
using VoiceDock.Services;
using InputMethod = VoiceDock.Models.InputMethod;

namespace VoiceDock.UI;

/// <summary>
/// 設定画面（ダークテーマ固定・タブ形式・即時反映）。
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
    private readonly Action<bool> _setHotkeyCapture;
    private readonly Action _openDataFolder;
    private readonly Action _reopen;
    private bool _initializing = true;

    /// <summary>いまキーの入力を待っているホットキーの欄（待っていなければ null）。</summary>
    private TextBox? _capturing;
    private readonly SpeechBridgeServer? _bridge;

    /// <summary>マイクの選択肢（先頭は「Windows の既定」＝装置 ID が空）。</summary>
    private readonly List<MicrophoneDevice> _micChoices = new();

    /// <summary>一覧を作り直している間は、選択の変化を設定に書かない。</summary>
    private bool _buildingMicList;

    public SettingsWindow(SettingsService settings,
        Func<HotkeySpec, bool> applyHotkey, Func<HotkeySpec, bool> applyUndoHotkey,
        Action openDictionary, Action openSnippets, Action openAppRules, Action checkUpdate,
        Action restartBrowser, Func<string?> getMicrophone,
        BackupService backup, Func<BackupContents, bool> restoreBackup, SpeechBridgeServer? bridge,
        Action<bool> setHotkeyCapture, Action openDataFolder, Action reopen)
    {
        InitializeComponent();
        WindowBehavior.Attach(this, "Settings", closeOnEscape: false);   // Esc は「閉じる」ボタン（IsCancel）が受ける
        _settings = settings;
        _setHotkeyCapture = setHotkeyCapture;
        _openDataFolder = openDataFolder;
        _reopen = reopen;
        // ホットキーの入力を待っている途中で閉じても、止めていたホットキーを戻す
        Closed += (_, _) => EndCapture();
        _applyHotkey = applyHotkey;
        _applyUndoHotkey = applyUndoHotkey;
        _openDictionary = openDictionary;
        _openSnippets = openSnippets;
        _openAppRules = openAppRules;
        _checkUpdate = checkUpdate;
        _restartBrowser = restartBrowser;
        _backup = backup;
        _restoreBackup = restoreBackup;

        // 今どのマイクが使われているか（最後に音声入力したときのもの）
        var mic = getMicrophone();
        MicrophoneText.Text = mic is { Length: > 0 }
            ? $"最後に使ったマイク: {mic}"
            : "最後に使ったマイク: 音声入力を一度行うと表示されます";

        // マイクの一覧は認識用ブラウザに調べてもらう（届いたら一覧を作り直す）
        _bridge = bridge;
        BuildMicList(bridge?.Microphones ?? Array.Empty<MicrophoneDevice>());
        if (bridge != null)
        {
            bridge.MicrophonesReported += OnMicrophonesReported;
            Closed += (_, _) => bridge.MicrophonesReported -= OnMicrophonesReported;
            if (bridge.IsConnected) _ = bridge.RequestMicrophonesAsync();
        }

        HotkeyBox.Text = settings.Current.Hotkey;
        UndoHotkeyBox.Text = settings.Current.UndoHotkey;
        StartupCheck.IsChecked = settings.Current.StartupEnabled;

        HotkeyModeCombo.Items.Add("押すたびに開始・停止を切り替える");
        HotkeyModeCombo.Items.Add("押している間だけ音声入力する");
        HotkeyModeCombo.SelectedIndex = settings.Current.HotkeyMode == HotkeyMode.PushToTalk ? 1 : 0;

        foreach (var sec in AppSettings.SilenceAutoStopChoices)
            SilenceCombo.Items.Add(sec == 0 ? "自動では止めない" : $"{sec} 秒");
        SilenceCombo.SelectedIndex = Math.Max(0,
            Array.IndexOf(AppSettings.SilenceAutoStopChoices, settings.Current.SilenceAutoStopSeconds));

        OverlayCombo.Items.Add("画面の下・中央");
        OverlayCombo.Items.Add("画面の上・中央");
        OverlayCombo.Items.Add("画面の右下");
        OverlayCombo.Items.Add("表示しない");
        OverlayCombo.SelectedIndex = settings.Current.OverlayPosition switch
        {
            OverlayPosition.TopCenter => 1,
            OverlayPosition.BottomRight => 2,
            OverlayPosition.Hidden => 3,
            _ => 0,
        };

        WidthCombo.Items.Add("認識結果のまま");
        WidthCombo.Items.Add("半角にそろえる（例: ３時 → 3時）");
        WidthCombo.Items.Add("全角にそろえる（例: 3時 → ３時）");
        WidthCombo.SelectedIndex = settings.Current.CharacterWidth switch
        {
            CharacterWidth.Half => 1,
            CharacterWidth.Full => 2,
            _ => 0,
        };

        NewlineCombo.Items.Add("Shift+Enter（おすすめ。ほとんどのアプリで改行になります）");
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

        InputMethodCombo.Items.Add("キー入力として送る（おすすめ）");
        InputMethodCombo.Items.Add("貼り付けで入れる（クリップボードを使う）");
        InputMethodCombo.SelectedIndex = settings.Current.InputMethod == InputMethod.Clipboard ? 1 : 0;

        RemoveSpacesCheck.IsChecked = settings.Current.RemoveSpaces;
        AutoPeriodCheck.IsChecked = settings.Current.AutoPeriod;
        QuestionCheck.IsChecked = settings.Current.QuestionByIntonation;
        RemovePunctuationCheck.IsChecked = settings.Current.RemoveRecognizerPunctuation;
        KanjiNumberCheck.IsChecked = settings.Current.KanjiToArabic;
        AutoUpdateCheck.IsChecked = settings.Current.AutoApplyUpdate;
        VoiceCommandsCheck.IsChecked = settings.Current.VoiceCommandsEnabled;
        KeyCommandsCheck.IsChecked = settings.Current.KeyCommandsEnabled;
        RemoveFillersCheck.IsChecked = settings.Current.RemoveFillers;
        SnippetsCheck.IsChecked = settings.Current.SnippetsEnabled;
        UndoCheck.IsChecked = settings.Current.UndoEnabled;
        SuggestUndoCheck.IsChecked = settings.Current.SuggestFrequentUndo;
        SoundCheck.IsChecked = settings.Current.SoundFeedback;
        LocalRecognitionCheck.IsChecked = settings.Current.PreferLocalRecognition;
        LogRecognitionCheck.IsChecked = settings.Current.LogRecognitionText;

        UpdateModeCombo.Items.Add("起動のたびに確認する（おすすめ）");
        UpdateModeCombo.Items.Add("起動時に確認する（1 日 1 回まで）");
        UpdateModeCombo.Items.Add("自動では確認しない（手動のみ）");
        UpdateModeCombo.SelectedIndex = settings.Current.UpdateCheckMode switch
        {
            UpdateCheckMode.DailyOnStartup => 1,
            UpdateCheckMode.Manual => 2,
            _ => 0,
        };
        // どこへ通信するのかが利用者から見えるようにする
        UpdateUrlText.Text = "VoiceDock が外部と通信するのは、次の 2 つだけです。使用状況などは送りません。\n" +
                             "・音声の認識（ブラウザの音声認識機能。上の設定で、この PC の中での認識に切り替えられます）\n" +
                             $"・新しい版の確認とダウンロード（確認先: {settings.Current.UpdateApiUrl}）";

        DataFolderText.Text = AppPaths.Root + (AppPaths.IsPortable
            ? "\nVoiceDock.exe と、この data フォルダーをまとめて写せば、設定ごと別の PC へ引っ越せます。"
            : "\nVoiceDock.exe の置き場所に書き込めないため、こちらに保存しています。");

        VersionText.Text = $"VoiceDock v{UpdateService.CurrentVersion}";
        UpdateUndoOptions();

        _initializing = false;

        // トレイメニューから認識言語を切り替えた場合も、開いている画面の表示を合わせる
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => _openDataFolder();

    /// <summary>取り消しをオフにしている間は、関連する項目を選べないようにする。</summary>
    private void UpdateUndoOptions() => UndoOptions.IsEnabled = UndoCheck.IsChecked == true;

    private void OverlayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var position = OverlayCombo.SelectedIndex switch
        {
            1 => OverlayPosition.TopCenter,
            2 => OverlayPosition.BottomRight,
            3 => OverlayPosition.Hidden,
            _ => OverlayPosition.BottomCenter,
        };
        _settings.Update(s => s.OverlayPosition = position);
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
        UpdateUndoOptions();
        _settings.Update(s =>
        {
            s.RemoveSpaces = RemoveSpacesCheck.IsChecked == true;
            s.AutoPeriod = AutoPeriodCheck.IsChecked == true;
            s.QuestionByIntonation = QuestionCheck.IsChecked == true;
            s.RemoveRecognizerPunctuation = RemovePunctuationCheck.IsChecked == true;
            s.KanjiToArabic = KanjiNumberCheck.IsChecked == true;
            s.AutoApplyUpdate = AutoUpdateCheck.IsChecked == true;
            s.VoiceCommandsEnabled = VoiceCommandsCheck.IsChecked == true;
            s.KeyCommandsEnabled = KeyCommandsCheck.IsChecked == true;
            s.RemoveFillers = RemoveFillersCheck.IsChecked == true;
            s.SnippetsEnabled = SnippetsCheck.IsChecked == true;
            s.UndoEnabled = UndoCheck.IsChecked == true;
            s.SuggestFrequentUndo = SuggestUndoCheck.IsChecked == true;
            s.SoundFeedback = SoundCheck.IsChecked == true;
            s.PreferLocalRecognition = LocalRecognitionCheck.IsChecked == true;
            s.LogRecognitionText = LogRecognitionCheck.IsChecked == true;
        });
    }

    private void SilenceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SilenceCombo.SelectedIndex < 0) return;
        var seconds = AppSettings.SilenceAutoStopChoices[SilenceCombo.SelectedIndex];
        _settings.Update(s => s.SilenceAutoStopSeconds = seconds);
    }

    private void WidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var width = WidthCombo.SelectedIndex switch
        {
            1 => CharacterWidth.Half,
            2 => CharacterWidth.Full,
            _ => CharacterWidth.AsIs,
        };
        _settings.Update(s => s.CharacterWidth = width);
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

    private void OnMicrophonesReported(IReadOnlyList<MicrophoneDevice> list) =>
        Dispatcher.BeginInvoke(() => BuildMicList(list));

    /// <summary>
    /// マイクの選択肢を作り直す。選んでいるマイクは装置 ID で探し、見つからなければ名前で探す
    /// （抜き差しで装置 ID が変わることがあるため。名前で見つかったら新しい ID に置き換える）。
    /// どちらでも見つからなければ「（見つかりません）」として残し、勝手に既定へ戻さない。
    /// </summary>
    private void BuildMicList(IReadOnlyList<MicrophoneDevice> devices)
    {
        _buildingMicList = true;
        try
        {
            var s = _settings.Current;
            _micChoices.Clear();
            MicCombo.Items.Clear();
            _micChoices.Add(new MicrophoneDevice("", "Windows の既定のマイク"));
            MicCombo.Items.Add("Windows の既定のマイク");
            foreach (var d in devices)
            {
                _micChoices.Add(d);
                MicCombo.Items.Add(d.Label);
            }

            int index = 0;
            bool missing = false;
            if (s.MicrophoneDeviceId.Length > 0)
            {
                index = _micChoices.FindIndex(d => d.Id == s.MicrophoneDeviceId);
                if (index < 0 && s.MicrophoneLabel.Length > 0)
                {
                    index = _micChoices.FindIndex(d => d.Id.Length > 0 && d.Label == s.MicrophoneLabel);
                    if (index > 0)
                    {
                        var found = _micChoices[index];
                        _settings.Update(x => { x.MicrophoneDeviceId = found.Id; x.MicrophoneLabel = found.Label; });
                    }
                }
                if (index < 0)
                {
                    var label = s.MicrophoneLabel.Length > 0 ? s.MicrophoneLabel : "選んでいたマイク";
                    _micChoices.Add(new MicrophoneDevice(s.MicrophoneDeviceId, s.MicrophoneLabel));
                    MicCombo.Items.Add(devices.Count > 0 ? $"{label}（見つかりません）" : label);
                    index = _micChoices.Count - 1;
                    missing = devices.Count > 0;
                }
            }
            MicCombo.SelectedIndex = index;
            MicWarning.Text = "選んでいたマイクが見つかりません。つなぎ直すか、別のマイクを選んでください（それまでは Windows の既定のマイクで認識します）。";
            MicWarning.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _buildingMicList = false;
        }
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _buildingMicList || MicCombo.SelectedIndex < 0) return;
        var chosen = _micChoices[MicCombo.SelectedIndex];
        if (chosen.Id == _settings.Current.MicrophoneDeviceId) return;
        MicWarning.Visibility = Visibility.Collapsed;
        _settings.Update(s =>
        {
            s.MicrophoneDeviceId = chosen.Id;
            s.MicrophoneLabel = chosen.Id.Length > 0 ? chosen.Label : "";
        });
    }

    private void RefreshMic_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge is not { IsConnected: true })
        {
            ToastWindow.Show("認識エンジンの準備中です。数秒待ってからもう一度押してください。");
            return;
        }
        _ = _bridge.RequestMicrophonesAsync();
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
        bool ok = MessageDialog.Show(this, "バックアップから復元",
            "このバックアップで、今の設定・辞書・定型文をすべて置き換えます。\n\n" +
            $"作成日時: {created}\n" +
            $"作成したバージョン: {contents.AppVersion ?? "不明"}\n" +
            $"辞書: {contents.Dictionary.Count} 件　定型文: {contents.Snippets.Count} 件\n\n" +
            "この操作は取り消せません。",
            MessageDialogKind.Danger, "置き換える", "キャンセル");
        if (!ok) return;

        if (!_restoreBackup(contents)) return;

        ToastWindow.Show($"バックアップから復元しました（辞書 {contents.Dictionary.Count} 件・定型文 {contents.Snippets.Count} 件）。");
        // この画面は復元前の設定を表示しているため、開き直して新しい内容を見せる
        Close();
        _reopen();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        bool ok = MessageDialog.Show(this, "設定の初期化",
            "すべての設定を初期状態に戻します（ホットキーやアプリ別の設定も戻ります）。\n辞書と定型文は消えません。",
            MessageDialogKind.Danger, "初期状態に戻す", "キャンセル");
        if (!ok) return;

        _settings.ResetToDefaults();
        ToastWindow.Show("設定を初期状態に戻しました。");
        Close();
        _reopen();
    }

    private void OpenSnippets_Click(object sender, RoutedEventArgs e) => _openSnippets();

    private void OpenAppRules_Click(object sender, RoutedEventArgs e) => _openAppRules();

    // ───────── ホットキーの変更 ─────────
    //
    // 欄をクリックするか「変更…」を押すと、キーの入力を待つ。待っている間だけキーを受け取るので、
    // ふだんは Tab や Esc がそのまま働く（以前は欄にフォーカスがあるだけで、すべてのキーを飲み込んでいた）。
    // 待っている間は、いま登録しているホットキーを止める（止めないと、今のホットキーを押し直したときに
    // 音声入力が始まってしまい、同じキーを選び直すこともできない）。

    private void HotkeyChange_Click(object sender, RoutedEventArgs e) => BeginCapture(HotkeyBox);

    private void UndoHotkeyChange_Click(object sender, RoutedEventArgs e) => BeginCapture(UndoHotkeyBox);

    private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;
        e.Handled = true;
        BeginCapture(box);
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _capturing)) EndCapture();
    }

    private void BeginCapture(TextBox box)
    {
        if (ReferenceEquals(_capturing, box)) return;
        EndCapture();
        _capturing = box;
        box.Text = "キーを押してください（Esc で取りやめ）";
        WarningFor(box).Visibility = Visibility.Collapsed;
        _setHotkeyCapture(true);
        box.Focus();
        Keyboard.Focus(box);
    }

    /// <summary>キーの入力待ちをやめ、欄の表示を今の設定に戻す。</summary>
    private void EndCapture()
    {
        if (_capturing is not { } box) return;
        _capturing = null;
        box.Text = ReferenceEquals(box, HotkeyBox) ? _settings.Current.Hotkey : _settings.Current.UndoHotkey;
        _setHotkeyCapture(false);
    }

    private TextBlock WarningFor(TextBox box) => ReferenceEquals(box, HotkeyBox) ? HotkeyWarning : UndoHotkeyWarning;

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(box, _capturing)) return;
        e.Handled = true;

        var key = ResolveKey(e);
        // 修飾キー単独は確定しない
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        bool isUndo = ReferenceEquals(box, UndoHotkeyBox);
        var changeButton = isUndo ? UndoHotkeyChangeButton : HotkeyChangeButton;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            EndCapture();
            changeButton.Focus();
            return;
        }

        // Keyboard.Modifiers は Win キーを返さないため、押されているかを別に確かめる
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        var spec = new HotkeySpec(modifiers, key);
        var warning = WarningFor(box);
        var other = isUndo ? _settings.Current.Hotkey : (_settings.Current.UndoEnabled ? _settings.Current.UndoHotkey : "");
        if (WhyUnusable(spec, other, isUndo) is { } reason)
        {
            warning.Text = reason;
            warning.Visibility = Visibility.Visible;
            return;
        }

        // いったん入力待ちをやめて（止めていたホットキーが戻る）、新しいキーを登録する
        EndCapture();
        bool applied = isUndo ? _applyUndoHotkey(spec) : _applyHotkey(spec);
        if (applied)
        {
            _settings.Update(s =>
            {
                if (isUndo) s.UndoHotkey = spec.ToString();
                else s.Hotkey = spec.ToString();
            });
            box.Text = spec.ToString();
            warning.Visibility = Visibility.Collapsed;
        }
        else
        {
            // 他アプリとの衝突。元のホットキーを復元登録済み（App 側）なので表示は変えない
            warning.Text = $"{spec} は、他のアプリが使っているため登録できませんでした。別のキーを指定してください。";
            warning.Visibility = Visibility.Visible;
        }
        changeButton.Focus();
    }

    /// <summary>ホットキーに使えない組み合わせなら、その理由を返す。使えるなら null。</summary>
    private static string? WhyUnusable(HotkeySpec spec, string otherHotkey, bool isUndo)
    {
        bool functionKey = spec.Key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll;
        if (spec.Modifiers == ModifierKeys.None && !functionKey)
            return "Ctrl・Alt・Shift・Win のどれかと組み合わせてください（F1〜F12 などは単独でも使えます）。";
        if (spec.Modifiers == ModifierKeys.Shift && !functionKey)
            return "Shift だけとの組み合わせは、文字の入力とぶつかるため使えません。Ctrl や Alt と組み合わせてください。";

        bool reserved = (spec.Modifiers, spec.Key) switch
        {
            (ModifierKeys.Alt, Key.F4 or Key.Tab or Key.Space or Key.Escape) => true,
            (ModifierKeys.Control, Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.A or Key.S or Key.Escape) => true,
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Escape) => true,
            (ModifierKeys.Control | ModifierKeys.Alt, Key.Delete) => true,
            (ModifierKeys.Windows, Key.L or Key.D or Key.E or Key.R or Key.Tab) => true,
            _ => false,
        };
        if (reserved)
            return $"{spec} は、コピーやウィンドウを閉じるなどの大事な操作に使われているため選べません。";

        if (HotkeySpec.TryParse(otherHotkey, out var other) && other == spec)
            return isUndo
                ? "音声入力のホットキーと同じキーは使えません。"
                : "取り消しのホットキーと同じキーは使えません。";
        return null;
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
