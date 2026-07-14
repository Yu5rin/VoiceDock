using System.Windows;
using System.Windows.Threading;
using VoiceDock.UI;

namespace VoiceDock.Services;

/// <summary>
/// 認識の開始/停止と、認識結果の入力欄への流し込みを統括する。
/// 実際の録音・認識はブラウザ(Web Speech API)が行い、本体は
/// 確定テキストを受け取って辞書置換のうえ SendInput で前面アプリへ入力する。
/// ホットキーのトグル、30 秒無音の自動停止、トレイ・オーバーレイ制御を担う。
/// </summary>
public sealed class RecordingController : IDisposable
{
    private static readonly TimeSpan SilenceAutoStop = TimeSpan.FromSeconds(30);

    private readonly LogService _log;
    private readonly DictionaryService _dictionary;
    private readonly SpeechBridgeServer _bridge;
    private readonly TrayIconController _tray;
    private readonly OverlayWindow _overlay;
    private readonly Dispatcher _dispatcher;

    private readonly object _sync = new();
    private bool _listening;
    private DispatcherTimer? _silenceTimer;

    public RecordingController(LogService log, DictionaryService dictionary,
        SpeechBridgeServer bridge, TrayIconController tray, OverlayWindow overlay)
    {
        _log = log;
        _dictionary = dictionary;
        _bridge = bridge;
        _tray = tray;
        _overlay = overlay;
        _dispatcher = Application.Current.Dispatcher;

        _bridge.FinalText += OnFinalText;
        _bridge.LevelChanged += _overlay.UpdateLevel;
        _bridge.RecognitionError += OnRecognitionError;
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

    private void StartListening()
    {
        if (!_bridge.IsConnected)
        {
            _log.Warn("認識ブラウザが未接続のため録音を開始できません");
            ToastWindow.Show("認識エンジンの準備がまだ完了していません。数秒待って再度お試しください。", ToastKind.Warning);
            return;
        }

        _listening = true;
        _ = _bridge.StartRecognitionAsync();
        _tray.SetState(TrayState.Recording);
        _overlay.ShowOverlay();
        ResetSilenceTimer();
        _log.Info("録音を開始しました");
    }

    private void StopListening(string reason)
    {
        if (!_listening) return;
        _listening = false;
        _ = _bridge.StopRecognitionAsync();
        StopSilenceTimer();
        _overlay.HideOverlay();
        _tray.SetState(TrayState.Idle);
        _log.Info($"録音を停止しました ({reason})");
    }

    private void OnFinalText(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;

        _dispatcher.BeginInvoke(() =>
        {
            lock (_sync)
            {
                if (!_listening) return;
                ResetSilenceTimer();
            }

            // 辞書の「誤認識語→正しい語」置換を適用
            text = _dictionary.ApplyReplacements(text);
            _log.Recognition(text);

            // フォーカス中のテキスト入力欄へ直接キー入力。
            // 入力欄が無い等で失敗した場合は何もしない（エラー通知なし）
            if (!TextInjector.SendText(text))
                _log.Info("テキスト入力欄が見つからないため流し込みをスキップしました");
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
                    _log.Info("30 秒間無音が続いたため録音を自動停止します");
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
