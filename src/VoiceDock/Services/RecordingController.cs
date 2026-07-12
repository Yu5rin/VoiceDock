using System.Windows;
using VoiceDock.UI;

namespace VoiceDock.Services;

/// <summary>
/// 録音〜文字起こし〜直接入力のオーケストレーション。
/// ホットキーのトグル（開始/停止）、30 秒無音の自動停止、
/// 状態に応じたトレイアイコン・オーバーレイの制御を行う。
/// </summary>
public sealed class RecordingController : IDisposable
{
    private enum State { Idle, Recording, Processing }

    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly TranscriptionService _transcription;
    private readonly ModelDownloader _modelDownloader;
    private readonly TrayIconController _tray;
    private readonly OverlayWindow _overlay;
    private readonly AudioRecorder _recorder = new();

    private readonly object _sync = new();
    private State _state = State.Idle;

    public RecordingController(LogService log, SettingsService settings,
        TranscriptionService transcription, ModelDownloader modelDownloader,
        TrayIconController tray, OverlayWindow overlay)
    {
        _log = log;
        _settings = settings;
        _transcription = transcription;
        _modelDownloader = modelDownloader;
        _tray = tray;
        _overlay = overlay;

        _recorder.LevelChanged += _overlay.UpdateLevel;
        _recorder.SilenceTimeout += OnSilenceTimeout;
        _recorder.RecordingFailed += OnRecordingFailed;
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
            switch (_state)
            {
                case State.Idle:
                    StartRecording();
                    break;
                case State.Recording:
                    _ = StopAndTranscribeAsync();
                    break;
                case State.Processing:
                    _log.Info("文字起こし処理中のためホットキーを無視しました");
                    break;
            }
        }
    }

    private void StartRecording()
    {
        try
        {
            int deviceIndex = AudioRecorder.ResolveDeviceIndex(_settings.Current.MicDeviceName);
            _recorder.Start(deviceIndex);
            _state = State.Recording;
            _tray.SetState(TrayState.Recording);
            _overlay.ShowOverlay();
            _log.Info("録音を開始しました");

            // モデル未取得ならバックグラウンドで取得しておく
            if (!ModelDownloader.IsModelReady(_settings.Current.ModelSize))
                _ = EnsureModelWithNotificationAsync(_settings.Current.ModelSize);
        }
        catch (Exception ex)
        {
            _state = State.Idle;
            _log.Error($"録音を開始できませんでした: {ex.Message}");
            _tray.SetState(TrayState.Error);
            ToastWindow.Show($"録音を開始できませんでした: {ex.Message}", ToastKind.Error);
            ResetTrayAfterDelay();
        }
    }

    /// <summary>30 秒無音時の自動停止（NAudio コールバックスレッドから呼ばれる）。</summary>
    private void OnSilenceTimeout()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            lock (_sync)
            {
                if (_state != State.Recording) return;
                _log.Info("30 秒間無音が続いたため録音を自動停止します");
                _ = StopAndTranscribeAsync();
            }
        });
    }

    private void OnRecordingFailed(Exception ex)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            lock (_sync)
            {
                if (_state != State.Recording) return;
                _state = State.Idle;
            }
            _recorder.Stop();
            _overlay.HideOverlay();
            _log.Error($"録音中にエラーが発生しました: {ex.Message}");
            _tray.SetState(TrayState.Error);
            ToastWindow.Show($"録音中にエラーが発生しました: {ex.Message}", ToastKind.Error);
            ResetTrayAfterDelay();
        });
    }

    private async Task StopAndTranscribeAsync()
    {
        float[] samples = _recorder.Stop();
        _state = State.Processing;
        _overlay.HideOverlay();
        _tray.SetState(TrayState.Idle, "VoiceDock - 文字起こし中");
        _log.Info($"録音を停止しました ({samples.Length / 16000.0:F1} 秒)");

        try
        {
            // VAD フィルタ（無音区間除去）
            var voiced = VadFilter.Trim(samples, 16000);
            if (voiced.Length == 0)
            {
                _log.Info("音声が検出されなかったため文字起こしをスキップしました");
                return;
            }

            var modelPath = await EnsureModelWithNotificationAsync(_settings.Current.ModelSize);

            var text = await Task.Run(() => _transcription.TranscribeAsync(voiced, modelPath));
            if (text.Length == 0)
            {
                _log.Info("認識結果が空でした");
                return;
            }

            _log.Recognition(text);

            // フォーカス中のテキスト入力欄へ直接キー入力。
            // 入力欄が無い等で失敗した場合は何もしない（エラー通知なし）
            if (!TextInjector.SendText(text))
                _log.Info("テキスト入力欄が見つからないため流し込みをスキップしました");
        }
        catch (Exception ex)
        {
            _log.Error($"文字起こしに失敗しました: {ex.Message}");
            _tray.SetState(TrayState.Error);
            ToastWindow.Show($"文字起こしに失敗しました: {ex.Message}", ToastKind.Error);
            ResetTrayAfterDelay();
        }
        finally
        {
            lock (_sync) _state = State.Idle;
            if (_tray != null) _tray.SetState(TrayState.Idle);
        }
    }

    /// <summary>初回起動時などのモデル自動ダウンロード（トースト・トレイ表示付き）。</summary>
    public async Task<string> EnsureModelWithNotificationAsync(string modelSize)
    {
        if (ModelDownloader.IsModelReady(modelSize))
            return ModelDownloader.GetModelPath(modelSize);

        ToastWindow.Show($"Whisper モデル ({modelSize}) をダウンロードしています。完了までしばらくお待ちください。");
        int lastPercent = -1;
        var progress = new Progress<double>(p =>
        {
            int percent = (int)(p * 100);
            if (percent / 10 != lastPercent / 10)
            {
                lastPercent = percent;
                _tray.SetState(TrayState.Idle, $"VoiceDock - モデルDL中 {percent}%");
            }
        });

        try
        {
            var path = await _modelDownloader.EnsureModelAsync(modelSize, progress);
            ToastWindow.Show($"モデル ({modelSize}) のダウンロードが完了しました。");
            return path;
        }
        catch (Exception ex)
        {
            _log.Error($"モデルのダウンロードに失敗しました: {ex.Message}");
            ToastWindow.Show($"モデルのダウンロードに失敗しました: {ex.Message}", ToastKind.Error);
            throw;
        }
        finally
        {
            _tray.SetState(TrayState.Idle);
        }
    }

    private void ResetTrayAfterDelay()
    {
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            lock (_sync)
            {
                if (_state == State.Idle)
                    _tray.SetState(TrayState.Idle);
                else if (_state == State.Recording)
                    _tray.SetState(TrayState.Recording);
            }
        });
    }

    public void Dispose()
    {
        _recorder.Dispose();
    }
}
