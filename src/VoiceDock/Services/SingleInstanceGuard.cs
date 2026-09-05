namespace VoiceDock.Services;

/// <summary>
/// 二重起動制御。名前付き Mutex で 1 インスタンスに制限し、
/// 2 個目の起動時は EventWaitHandle 経由で既存インスタンスへ「前面化」を通知して終了する。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\VoiceDock_SingleInstance";
    private const string EventName = @"Local\VoiceDock_Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _listenerThread;
    private volatile bool _disposed;

    /// <summary>このプロセスが最初のインスタンスなら true。</summary>
    public bool IsPrimaryInstance { get; private set; }

    /// <summary>既存インスタンスに対して 2 個目の起動が通知されたときに発火（リスナースレッド上）。</summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// 単独起動の権利を取得する。
    /// </summary>
    /// <param name="waitFor">
    /// 既存インスタンスの終了を待つ時間。自動更新の直後は、旧バージョンがまだ終了しきって
    /// いないうちに新バージョンが起動するため、待たずに諦めると「更新したらアプリが消える」
    /// ことになる。そのため更新直後だけ待機してから判定する。
    /// </param>
    public bool TryAcquire(TimeSpan waitFor = default)
    {
        bool createdNew = TryCreateMutex();

        if (!createdNew && waitFor > TimeSpan.Zero)
        {
            var deadline = DateTime.UtcNow + waitFor;
            while (!createdNew && DateTime.UtcNow < deadline)
            {
                _mutex?.Dispose();
                _mutex = null;
                Thread.Sleep(200);
                createdNew = TryCreateMutex();
            }
        }

        IsPrimaryInstance = createdNew;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        if (createdNew)
        {
            _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "VoiceDockActivateListener" };
            _listenerThread.Start();
        }
        else
        {
            // 既存インスタンスへ通知して自分は終了する
            _activateEvent.Set();
        }
        return createdNew;
    }

    private bool TryCreateMutex()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        return createdNew;
    }

    private void ListenLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (_activateEvent!.WaitOne(TimeSpan.FromSeconds(1)))
                    ActivationRequested?.Invoke();
            }
            catch
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (IsPrimaryInstance)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* 所有していない場合は無視 */ }
        }
        _mutex?.Dispose();
        _activateEvent?.Dispose();
    }
}
