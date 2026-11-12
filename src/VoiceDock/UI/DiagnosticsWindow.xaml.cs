using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 動作チェック画面。「文字が入らない」「認識しない」ときに、原因の見当を付けるため、
/// 認識エンジン・マイク・ホットキー・入力先アプリの設定と IME の状態を一覧にする。
/// テスト入力で、音声入力と同じ経路で実際に文字を入れて確かめることもできる。
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private const int CountdownSeconds = 5;
    private const string TestText = "VoiceDock のテスト入力です。123 ABC";

    private readonly SettingsService _settings;
    private readonly SpeechBridgeServer? _bridge;
    private readonly BrowserLauncher? _browser;
    private readonly HotkeyManager? _hotkey;
    private readonly RecordingController _controller;
    private readonly Action _showLog;

    /// <summary>「結果をコピー」で書き出す内容（区分ごと）。</summary>
    private readonly StringBuilder _statusReport = new();
    private readonly StringBuilder _probeReport = new();
    private string _testReport = "";

    private DispatcherTimer? _countdown;

    public DiagnosticsWindow(SettingsService settings, SpeechBridgeServer? bridge, BrowserLauncher? browser,
        HotkeyManager? hotkey, RecordingController controller, Action showLog)
    {
        InitializeComponent();
        WindowBehavior.Attach(this, "Diagnostics", closeOnEscape: false);   // Esc は「閉じる」ボタン（IsCancel）が受ける
        // 設定を変えて戻ってきたときなどに、古い状態を見せたままにしない
        Activated += (_, _) => RefreshStatus();
        _settings = settings;
        _bridge = bridge;
        _browser = browser;
        _hotkey = hotkey;
        _controller = controller;
        _showLog = showLog;
        RefreshStatus();
    }

    // ───────────── VoiceDock の状態 ─────────────

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        var s = _settings.Current;
        StatusPanel.Children.Clear();
        _statusReport.Clear();

        AddRow(StatusPanel, _statusReport, "バージョン", $"v{UpdateService.CurrentVersion}", null);
        AddRow(StatusPanel, _statusReport, "保存先",
            AppPaths.Root + (AppPaths.IsPortable ? "" : "（exe の場所に書き込めないため、こちらに保存しています）"), null);

        bool connected = _bridge?.IsConnected == true;
        AddRow(StatusPanel, _statusReport, "認識エンジン",
            connected ? "つながっています" : "つながっていません（起動直後なら数秒待って［最新の状態にする］。続くときは、トレイメニューの「トラブル解決 → 認識エンジンを再起動」）",
            connected);
        AddRow(StatusPanel, _statusReport, "認識用ブラウザ", _browser?.LaunchedBrowserName ?? "起動していません",
            _browser?.LaunchedBrowserName != null ? null : false);

        var mic = _bridge?.CurrentMicrophone;
        AddRow(StatusPanel, _statusReport, "最後に使ったマイク",
            mic ?? "まだ分かりません（音声入力を一度行うと表示されます）", null);

        AddRow(StatusPanel, _statusReport, "設定で選んだマイク",
            s.MicrophoneDeviceId.Length > 0 ? s.MicrophoneLabel : "Windows の既定のマイク", null);
        AddRow(StatusPanel, _statusReport, "認識言語",
            RecognitionLanguages.LabelOf(s.RecognitionLanguage) + (s.PreferLocalRecognition ? "（できるだけ、この PC の中で認識）" : ""), null);

        var mode = s.HotkeyMode == HotkeyMode.PushToTalk ? "押している間だけ" : "押すたびに開始・停止";
        bool recordOk = _hotkey?.IsRecordRegistered == true;
        AddRow(StatusPanel, _statusReport, "音声入力のホットキー",
            $"{s.Hotkey}（{mode}）— " + (recordOk ? "使えます" : "他のアプリが使っているため、登録できていません。設定で別のキーにしてください"),
            recordOk);

        if (s.UndoEnabled)
        {
            bool undoOk = _hotkey?.IsUndoRegistered == true;
            AddRow(StatusPanel, _statusReport, "取り消しのホットキー",
                $"{s.UndoHotkey} — " + (undoOk ? "使えます" : "他のアプリが使っているため、登録できていません。設定で別のキーにしてください"), undoOk);
        }
        else
        {
            AddRow(StatusPanel, _statusReport, "取り消しのホットキー", "取り消しはオフにしています", null);
        }

        AddRow(StatusPanel, _statusReport, "文字の入れ方", MethodLabel(s.InputMethod), null);
        AddRow(StatusPanel, _statusReport, "改行のキー", NewlineLabel(s.NewlineMode), null);
        int appRules = s.AppInputMethods.Keys.Concat(s.AppNewlineModes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        AddRow(StatusPanel, _statusReport, "アプリ別の設定", appRules == 0 ? "なし" : $"{appRules} 件", null);
        AddRow(StatusPanel, _statusReport, "音声入力", _controller.IsListening ? "入力中" : "停止中", null);
    }

    // ───────────── 入力先のアプリ ─────────────

    private void Probe_Click(object sender, RoutedEventArgs e) =>
        StartCountdown(ProbeCountdown, "秒後に調べます。調べたいアプリの入力欄をクリックしてください", Probe);

    private void Probe()
    {
        var s = _settings.Current;
        ProbePanel.Children.Clear();
        _probeReport.Clear();

        if (IsSelfForeground())
        {
            AddRow(ProbePanel, _probeReport, "前面のアプリ",
                "この画面が前面のままでした。ボタンを押した後、5 秒以内に調べたいアプリの入力欄をクリックしてください。", false);
            return;
        }

        var app = TextInjector.GetForegroundProcessName();
        AddRow(ProbePanel, _probeReport, "前面のアプリ", app.Length > 0 ? app : "取得できませんでした", app.Length > 0 ? null : false);

        bool hasFocus = TextInjector.HasTextInputFocus();
        AddRow(ProbePanel, _probeReport, "入力欄",
            hasFocus ? "見つかりました" : "見つかりません（文字を入力できる欄をクリックしてから試してください）", hasFocus);

        bool methodOverride = app.Length > 0 && s.AppInputMethods.ContainsKey(app);
        AddRow(ProbePanel, _probeReport, "文字の入れ方",
            MethodLabel(_controller.ResolveInputMethod(app)) + (methodOverride ? "（アプリ別の設定）" : "（共通の設定）"), null);

        bool newlineOverride = app.Length > 0 && s.AppNewlineModes.ContainsKey(app);
        AddRow(ProbePanel, _probeReport, "改行のキー",
            NewlineLabel(_controller.ResolveNewlineMode(app)) + (newlineOverride ? "（アプリ別の設定）" : "（共通の設定）"), null);

        var ime = TextInjector.GetForegroundImeOpen();
        AddRow(ProbePanel, _probeReport, "IME",
            ime switch
            {
                true => "オン（音声入力の間は自動でオフにし、止めた後に戻します）",
                false => "オフ",
                null => "取得できませんでした（IME を使わないアプリか、入力欄が無い）",
            }, null);

        BringBack();
    }

    // ───────────── テスト入力 ─────────────

    private void Test_Click(object sender, RoutedEventArgs e) =>
        StartCountdown(TestCountdown, "秒後に入力します。試したいアプリの入力欄をクリックしてください", RunTest);

    private void RunTest()
    {
        if (IsSelfForeground())
        {
            TestResult.Foreground = (Brush)FindResource("WarnBrush");
            TestResult.Text = "⚠ この画面が前面のままでした。ボタンを押した後、5 秒以内に試したいアプリの入力欄をクリックしてください。";
            _testReport = "テスト入力: " + TestResult.Text;
            return;
        }

        var app = _controller.InjectTest(TestText);
        if (app.Length > 0)
        {
            TestResult.Foreground = (Brush)FindResource("FgBrush");
            TestResult.Text = $"「{app}」に入力しました。「{TestText}」がそのまま入ったか確認してください。" +
                              "文字が欠ける・入れ替わるときは、「アプリ別の設定」で、そのアプリの文字の入れ方を「貼り付けで入れる」にしてみてください。";
        }
        else
        {
            TestResult.Foreground = (Brush)FindResource("WarnBrush");
            TestResult.Text = "⚠ 入力欄が見つからず、入力できませんでした。文字を入力できる欄をクリックしてから試してください。";
        }
        _testReport = "テスト入力: " + TestResult.Text;
        BringBack();
    }

    // ───────────── 共通 ─────────────

    /// <summary>
    /// 数秒の猶予を置いてから処理する。その間に利用者が調べたいアプリへ切り替えられるようにするため。
    /// </summary>
    private void StartCountdown(TextBlock label, string message, Action action)
    {
        _countdown?.Stop();
        ProbeButton.IsEnabled = false;
        TestButton.IsEnabled = false;
        ProbeCountdown.Text = "";
        TestCountdown.Text = "";

        int remaining = CountdownSeconds;
        label.Text = $"{remaining} {message}";
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                label.Text = $"{remaining} {message}";
                return;
            }
            _countdown!.Stop();
            label.Text = "";
            ProbeButton.IsEnabled = true;
            TestButton.IsEnabled = true;
            action();
        };
        _countdown.Start();
    }

    /// <summary>前面が VoiceDock 自身（この画面など）のままかどうか。</summary>
    private static bool IsSelfForeground() =>
        string.Equals(TextInjector.GetForegroundProcessName(),
            System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>結果を見てもらえるよう、この画面を前に戻す（戻せない場合はタスクバーで点滅する）。</summary>
    private void BringBack()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void AddRow(Panel panel, StringBuilder report, string label, string value, bool? ok)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var mark = ok switch { true => "✓ ", false => "⚠ ", null => "" };
        var labelBlock = new TextBlock { Text = label, Foreground = (Brush)FindResource("SubFgBrush"), TextWrapping = TextWrapping.Wrap };
        var valueBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (mark.Length > 0)
        {
            valueBlock.Inlines.Add(new System.Windows.Documents.Run(mark)
            {
                Foreground = (Brush)FindResource(ok == true ? "SuccessBrush" : "WarnBrush"),
            });
        }
        valueBlock.Inlines.Add(new System.Windows.Documents.Run(value)
        {
            Foreground = (Brush)FindResource(ok == false ? "WarnBrush" : "FgBrush"),
        });
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        panel.Children.Add(grid);

        report.AppendLine($"{label}: {mark}{value}");
    }

    private static string MethodLabel(InputMethod method) =>
        method == InputMethod.Clipboard ? "貼り付けで入れる（クリップボードを使う）" : "キー入力として送る";

    private static string NewlineLabel(NewlineMode mode) => mode switch
    {
        NewlineMode.Enter => "Enter",
        NewlineMode.AltEnter => "Alt+Enter",
        _ => "Shift+Enter",
    };

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"VoiceDock 動作チェック（{DateTime.Now:yyyy/MM/dd HH:mm}）");
        sb.AppendLine();
        sb.Append(_statusReport);
        if (_probeReport.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[入力先のアプリ]");
            sb.Append(_probeReport);
        }
        if (_testReport.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(_testReport);
        }
        try
        {
            Clipboard.SetDataObject(sb.ToString(), true);
            ToastWindow.Show("動作チェックの結果をコピーしました。");
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"コピーできませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void ShowLog_Click(object sender, RoutedEventArgs e) => _showLog();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object sender, EventArgs e) => _countdown?.Stop();
}
