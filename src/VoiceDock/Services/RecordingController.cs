using System.Windows;
using System.Windows.Threading;
using VoiceDock.Models;
using VoiceDock.UI;

namespace VoiceDock.Services;

/// <summary>
/// 認識の開始/停止と、認識結果の入力欄への流し込みを統括する。
/// 実際の録音・認識はブラウザ(Web Speech API)が行い、本体は
/// 確定テキストを受け取って後処理（音声コマンド・辞書置換・整形）のうえ前面アプリへ入力する。
/// ホットキーのトグル、30 秒無音の自動停止、トレイ・オーバーレイ制御を担う。
/// </summary>
public sealed class RecordingController : IDisposable
{
    private static readonly TimeSpan SilenceAutoStop = TimeSpan.FromSeconds(30);

    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly TextProcessor _processor;
    private readonly SpeechBridgeServer _bridge;
    private readonly TrayIconController _tray;
    private readonly OverlayWindow _overlay;
    private readonly Dispatcher _dispatcher;

    private readonly object _sync = new();
    private bool _listening;
    private DispatcherTimer? _silenceTimer;
    private bool _localModeNotified;
    private bool _localFallbackNotified;

    /// <summary>
    /// 直前に入力したテキストの「見た目の文字数」（取り消し用）。0 なら取り消す対象なし。
    /// 絵文字などのサロゲートペアは 2 つの char で 1 文字のため、char 数ではなく
    /// 書記素（text element）数で数える。そうしないと BackSpace を余分に送ってしまう。
    /// </summary>
    private int _lastInjectedLength;

    /// <summary>直前に入力した先のアプリ（プロセス名）。別アプリでの誤爆を防ぐために使う。</summary>
    private string _lastInjectedApp = "";

    /// <summary>入力先として観測したアプリ（プロセス名）。アプリ別設定の候補に使う。</summary>
    private readonly SortedSet<string> _seenApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>これまでに入力先となったアプリのプロセス名一覧。</summary>
    public IReadOnlyList<string> SeenApps
    {
        get { lock (_sync) return _seenApps.ToList(); }
    }

    /// <summary>録音状態の変化（トレイメニューの表記更新用）。</summary>
    public event Action<bool>? ListeningChanged;

    public bool IsListening
    {
        get { lock (_sync) return _listening; }
    }

    public RecordingController(LogService log, SettingsService settings, TextProcessor processor,
        SpeechBridgeServer bridge, TrayIconController tray, OverlayWindow overlay)
    {
        _log = log;
        _settings = settings;
        _processor = processor;
        _bridge = bridge;
        _tray = tray;
        _overlay = overlay;
        _dispatcher = Application.Current.Dispatcher;

        _bridge.FinalText += OnFinalText;
        _bridge.PartialText += _overlay.SetPartialText;
        _bridge.LevelChanged += _overlay.UpdateLevel;
        _bridge.RecognitionError += OnRecognitionError;
        _bridge.RecognitionModeReported += OnRecognitionModeReported;
    }

    /// <summary>ホットキー押下時の入口。UI スレッドで呼ぶこと。</summary>
    public void Toggle()
    {
        // IME 変換中（未確定文字列がある状態）のホットキーは無視する
        if (ImeGuard.IsImeComposing())
        {
            _log.Info("IME 変換中のためホットキーを無視しました");
            return;
        }

        lock (_sync)
        {
            if (_listening) StopListening("ホットキー");
            else StartListening();
        }
    }

    /// <summary>トレイメニュー等からの開始/停止。UI スレッドで呼ぶこと。</summary>
    public void ToggleFromMenu()
    {
        lock (_sync)
        {
            if (_listening) StopListening("メニュー");
            else StartListening();
        }
    }

    /// <summary>
    /// 押している間だけ録音する方式で、ホットキーが押されたときの入口。
    /// IME 変換中は無視する。
    /// </summary>
    public void BeginPushToTalk()
    {
        if (ImeGuard.IsImeComposing())
        {
            _log.Info("IME 変換中のためホットキーを無視しました");
            return;
        }

        lock (_sync)
        {
            if (!_listening) StartListening();
        }
    }

    /// <summary>押しっぱなし方式で、ホットキーが離されたときの入口。</summary>
    public void EndPushToTalk()
    {
        lock (_sync)
        {
            if (_listening) StopListening("キーを離した");
        }
    }

    private void StartListening()
    {
        if (!_bridge.IsConnected)
        {
            _log.Warn("認識ブラウザが未接続のため音声入力を開始できません");
            ToastWindow.Show("認識エンジンの準備中です。数秒待ってからお試しください。", ToastKind.Warning);
            return;
        }

        _listening = true;
        _ = _bridge.StartRecognitionAsync(_settings.Current.PreferLocalRecognition);
        _tray.SetState(TrayState.Recording);
        _overlay.ShowOverlay();
        ResetSilenceTimer();
        if (_settings.Current.SoundFeedback) SoundFeedback.PlayStart();
        _log.Info("音声入力を開始しました");
        ListeningChanged?.Invoke(true);
    }

    private void StopListening(string reason)
    {
        if (!_listening) return;
        _listening = false;
        _ = _bridge.StopRecognitionAsync();
        StopSilenceTimer();
        _overlay.HideOverlay();
        _tray.SetState(TrayState.Idle);
        if (_settings.Current.SoundFeedback) SoundFeedback.PlayStop();
        _log.Info($"音声入力を停止しました ({reason})");
        ListeningChanged?.Invoke(false);
    }

    private void OnFinalText(string raw)
    {
        // 認識確定から入力までの体感遅延を減らすため、通常優先度のキュー待ちを
        // 挟まず最優先(Send)で処理する。
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            lock (_sync)
            {
                if (!_listening) return;
                ResetSilenceTimer();
            }

            _overlay.ClearPartialText();

            var processed = _processor.Process(raw);

            // 「取り消し」コマンド: 直前に入力した文字数ぶん削除する
            if (processed.Kind == ProcessedKind.Undo)
            {
                UndoLastInjection("音声コマンド");
                return;
            }

            if (processed.Text.Length == 0) return;

            if (processed.Kind == ProcessedKind.Command)
                _log.Info($"音声コマンド「{processed.CommandName}」を実行しました");
            else
                _log.Recognition(processed.Text);

            Inject(processed.Text, isCommand: processed.Kind == ProcessedKind.Command);
        }));
    }

    /// <summary>
    /// テキストを前面アプリへ入力する。入力方式はアプリ別設定があればそれを優先する。
    /// 入力欄が無い等で失敗した場合は何もしない（エラー通知なし）。
    /// </summary>
    private void Inject(string text, bool isCommand)
    {
        var app = TextInjector.GetForegroundProcessName();
        if (app.Length > 0)
        {
            lock (_sync) _seenApps.Add(app);
        }

        var method = ResolveInputMethod(app);

        // 改行やタブなどの操作系はクリップボード貼り付けに向かないため直接入力する
        bool ok = method == InputMethod.Clipboard && !isCommand
            ? TextInjector.SendViaClipboard(text)
            : TextInjector.SendText(text, _settings.Current.NewlineMode);

        if (!ok)
        {
            _log.Info("テキスト入力欄が見つからないため流し込みをスキップしました");
            _lastInjectedLength = 0;
            _lastInjectedApp = "";
            return;
        }

        _lastInjectedLength = CountTextElements(text);
        _lastInjectedApp = app;
    }

    /// <summary>絵文字・結合文字を 1 文字として数える（BackSpace の回数に合わせるため）。</summary>
    private static int CountTextElements(string text)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (enumerator.MoveNext()) count++;
        return count;
    }

    /// <summary>アプリ別の入力方式の上書きがあればそれを、無ければ既定の入力方式を返す。</summary>
    private InputMethod ResolveInputMethod(string appName)
    {
        var s = _settings.Current;
        if (appName.Length > 0 && s.AppInputMethods.TryGetValue(appName, out var perApp))
            return perApp;
        return s.InputMethod;
    }

    /// <summary>直前に入力したテキストを取り消す（入力した文字数ぶん BackSpace を送る）。</summary>
    public void UndoLastInjection(string reason)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (!_settings.Current.UndoEnabled) return;

            int count = _lastInjectedLength;
            if (count <= 0)
            {
                _log.Info("取り消せる入力がありません");
                ToastWindow.Show("取り消せる入力がありません。");
                return;
            }

            // 入力した直後に別のアプリへ移っている場合、そこで BackSpace を送ると
            // 無関係な文字を消してしまう。入力先が変わっていたら取り消さない。
            var current = TextInjector.GetForegroundProcessName();
            if (_lastInjectedApp.Length > 0 &&
                !string.Equals(current, _lastInjectedApp, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"入力先が変わっているため取り消しを中止しました（入力時: {_lastInjectedApp} / 現在: {current}）");
                ToastWindow.Show($"入力したアプリ（{_lastInjectedApp}）が前面にないため、取り消しを中止しました。", ToastKind.Warning);
                return;
            }

            if (TextInjector.SendBackspaces(count))
                _log.Info($"直前の入力 {count} 文字を取り消しました ({reason})");
            else
                _log.Info("取り消し先の入力欄が見つかりませんでした");

            // 二重に取り消さないようクリアする
            _lastInjectedLength = 0;
            _lastInjectedApp = "";
        }));
    }

    /// <summary>
    /// 実際の認識モードの通知。ローカル処理を希望したのに使えなかった場合は、
    /// 毎回うるさくならないよう起動後 1 回だけ通知する。
    /// </summary>
    private void OnRecognitionModeReported(string mode)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (mode)
            {
                case "local":
                    if (!_localModeNotified)
                    {
                        _localModeNotified = true;
                        ToastWindow.Show("端末内で音声認識しています（音声はクラウドに送信されません）。");
                    }
                    _log.Info("認識モード: 端末内処理 (processLocally)");
                    break;
                case "downloading":
                    if (!_localFallbackNotified)
                    {
                        _localFallbackNotified = true;
                        ToastWindow.Show("端末内認識の言語パックを取得しています。完了までクラウド認識で動作します。", ToastKind.Warning);
                    }
                    _log.Info("認識モード: クラウド（端末内認識の言語パックを取得中）");
                    break;
                case "unsupported":
                    if (!_localFallbackNotified)
                    {
                        _localFallbackNotified = true;
                        ToastWindow.Show("このブラウザは端末内認識に対応していないため、クラウド認識で動作します。", ToastKind.Warning);
                    }
                    _log.Info("認識モード: クラウド（端末内認識は非対応）");
                    break;
                default:
                    _log.Info("認識モード: クラウド");
                    break;
            }
        });
    }

    private void OnRecognitionError(string detail)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (detail)
            {
                case "not-allowed":
                case "service-not-allowed":
                    _log.Error($"マイクの使用が許可されていません ({detail})");
                    _tray.SetState(TrayState.Error);
                    ToastWindow.Show("マイクの使用が許可されていません。ブラウザのマイク権限を確認してください。", ToastKind.Error);
                    break;
                case "speech-unsupported":
                    _log.Error("このブラウザは Web Speech API に対応していません");
                    _tray.SetState(TrayState.Error);
                    ToastWindow.Show("認識ブラウザが Web Speech API に対応していません。Edge/Chrome をご利用ください。", ToastKind.Error);
                    break;
                case "network":
                    _log.Warn("認識でネットワークエラーが発生しました");
                    ToastWindow.Show("音声認識のネットワークエラーが発生しました。接続を確認してください。", ToastKind.Warning);
                    break;
                default:
                    _log.Warn($"認識エラー: {detail}");
                    break;
            }
        });
    }

    private void ResetSilenceTimer()
    {
        _dispatcher.BeginInvoke(() =>
        {
            StopSilenceTimer();
            _silenceTimer = new DispatcherTimer { Interval = SilenceAutoStop };
            _silenceTimer.Tick += (_, _) =>
            {
                lock (_sync)
                {
                    if (!_listening) return;
                    _log.Info("30 秒間無音が続いたため音声入力を自動停止します");
                    StopListening("無音自動停止");
                }
            };
            _silenceTimer.Start();
        });
    }

    private void StopSilenceTimer()
    {
        _silenceTimer?.Stop();
        _silenceTimer = null;
    }

    public void Dispose()
    {
        StopSilenceTimer();
    }
}
