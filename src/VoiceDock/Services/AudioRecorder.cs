using NAudio.Wave;

namespace VoiceDock.Services;

/// <summary>
/// マイク録音。16kHz / 16bit / モノラルで取り込み、float 配列として蓄積する。
/// 音量レベルの通知（オーバーレイの波形用）と、30 秒無音の自動停止判定も行う。
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    /// <summary>これを下回る RMS を無音とみなす</summary>
    private const float SilenceRmsThreshold = 0.010f;
    private static readonly TimeSpan SilenceAutoStop = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private WaveInEvent? _waveIn;
    private List<float> _samples = new();
    private DateTime _lastVoiceTime;
    private bool _silenceNotified;

    public bool IsRecording { get; private set; }

    /// <summary>録音バッファ更新ごとの音量レベル (RMS 0..1)。NAudio のコールバックスレッドで発火する。</summary>
    public event Action<float>? LevelChanged;

    /// <summary>30 秒間無音が続いたときに 1 回だけ発火する。</summary>
    public event Action? SilenceTimeout;

    /// <summary>録音デバイスが利用中に失われた等の致命的エラー。</summary>
    public event Action<Exception>? RecordingFailed;

    public static List<(int Index, string Name)> GetDevices()
    {
        var list = new List<(int, string)>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add((i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }

    /// <summary>設定されたデバイス名からデバイス番号を解決する。見つからなければ既定 (0)。</summary>
    public static int ResolveDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return 0;
        foreach (var (index, name) in GetDevices())
            if (name == deviceName) return index;
        return 0;
    }

    public void Start(int deviceIndex)
    {
        lock (_sync)
        {
            if (IsRecording) return;
            if (WaveInEvent.DeviceCount == 0)
                throw new InvalidOperationException("マイクが見つかりません。マイクの接続を確認してください。");

            _samples = new List<float>();
            _lastVoiceTime = DateTime.UtcNow;
            _silenceNotified = false;

            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Min(deviceIndex, WaveInEvent.DeviceCount - 1),
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            IsRecording = true;
        }
    }

    /// <summary>録音を停止し、それまでの音声サンプル (16kHz mono float) を返す。</summary>
    public float[] Stop()
    {
        lock (_sync)
        {
            if (!IsRecording) return Array.Empty<float>();
            IsRecording = false;
            try
            {
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.RecordingStopped -= OnRecordingStopped;
                    _waveIn.StopRecording();
                    _waveIn.Dispose();
                }
            }
            finally
            {
                _waveIn = null;
            }
            return _samples.ToArray();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        if (count == 0) return;

        double sumSquares = 0;
        lock (_sync)
        {
            if (!IsRecording) return;
            _samples.EnsureCapacity(_samples.Count + count);
            for (int i = 0; i < count; i++)
            {
                short s = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                float f = s / 32768f;
                _samples.Add(f);
                sumSquares += f * f;
            }
        }

        float rms = (float)Math.Sqrt(sumSquares / count);
        LevelChanged?.Invoke(rms);

        if (rms >= SilenceRmsThreshold)
        {
            _lastVoiceTime = DateTime.UtcNow;
        }
        else if (!_silenceNotified && DateTime.UtcNow - _lastVoiceTime >= SilenceAutoStop)
        {
            _silenceNotified = true;
            SilenceTimeout?.Invoke();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && IsRecording)
            RecordingFailed?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }
}
