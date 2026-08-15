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

    private void StartListening()
    {
        if (!_bridge.IsConnected)
        {
            _log.Warn("認識ブラウザが未接続のため録音を開始できません");
            ToastWindow.Show("認識エンジンの準備がまだ完了していません。数秒待って再度お試しください。", ToastKind.Warning);
            return;
        }

        _listening = true;
        _ = _bridge.StartRecognitionAsync(_settings.Current.PreferLocalRecognition);
        _tray.SetState(TrayState.Recording);
        _overlay.ShowOverlay();
        ResetSilenceTimer();
        if (_settings.Current.SoundFeedback) SoundFeedback.PlayStart();
        _log.Info("録音を開始しました");
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
        _log.Info($"録音を停止しました ({reason})");
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
            if (processed.Text.Length == 0) return;

            if (processed.IsCommand)
                _log.Info($"音声コマンド「{processed.CommandName}」を実行しました");
            else
                _log.Recognition(processed.Text);

            // フォーカス中のテキスト入力欄へ入力。
            // 入力欄が無い等で失敗した場合は何もしない（エラー通知なし）
            bool ok = _settings.Current.InputMethod == InputMethod.Clipboard && !processed.IsCommand
                ? TextInjector.SendViaClipboard(processed.Text)
                : TextInjector.SendText(processed.Text);
            if (!ok)
                _log.Info("テキスト入力欄が見つからないため流し込みをスキップしました");
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
