using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// グローバルホットキーの登録。RegisterHotKey を用い、
/// 登録失敗（他アプリとの衝突）を検出して呼び出し元へ返す。
/// 録音用と取り消し用の 2 つを扱う。
///
/// RegisterHotKey はキーを押した瞬間しか通知しないため、押している間だけ録音する
/// 方式（Push-to-talk）では、押下後にキー状態をポーリングして離されたことを検出する。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int RecordHotkeyId = 0xB00C;
    private const int UndoHotkeyId = 0xB00D;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;
    private bool _recordRegistered;
    private bool _undoRegistered;

    private DispatcherTimer? _releaseTimer;
    private uint _recordVk;

    /// <summary>録音ホットキーが押された。</summary>
    public event Action? HotkeyPressed;

    /// <summary>録音ホットキーが離された（押しっぱなし方式のときのみ発火）。</summary>
    public event Action? HotkeyReleased;

    /// <summary>取り消しホットキーが押された。</summary>
    public event Action? UndoPressed;

    /// <summary>押している間だけ録音する方式かどうか。</summary>
    public bool PushToTalk { get; set; }

    /// <summary>
    /// 録音ホットキーを登録する。既存の登録は解除される。
    /// 他アプリと衝突している場合は false を返す（登録は行われない）。
    /// </summary>
    public bool TryRegister(HotkeySpec spec)
    {
        EnsureWindow();
        Unregister();

        _recordVk = (uint)KeyInterop.VirtualKeyFromKey(spec.Key);
        // 押しっぱなし方式では、押下中のリピート通知は不要なので MOD_NOREPEAT のままでよい
        _recordRegistered = RegisterHotKey(_source!.Handle, RecordHotkeyId, ToModifiers(spec), _recordVk);
        return _recordRegistered;
    }

    /// <summary>
    /// 取り消しホットキーを登録する。衝突時は false（録音側の登録には影響しない）。
    /// </summary>
    public bool TryRegisterUndo(HotkeySpec spec)
    {
        EnsureWindow();
        UnregisterUndo();

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(spec.Key);
        _undoRegistered = RegisterHotKey(_source!.Handle, UndoHotkeyId, ToModifiers(spec), vk);
        return _undoRegistered;
    }

    private static uint ToModifiers(HotkeySpec spec)
    {
        uint modifiers = MOD_NOREPEAT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (spec.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;
        return modifiers;
    }

    public void Unregister()
    {
        StopReleaseWatch();
        if (_recordRegistered && _source != null)
        {
            UnregisterHotKey(_source.Handle, RecordHotkeyId);
            _recordRegistered = false;
        }
    }

    public void UnregisterUndo()
    {
        if (_undoRegistered && _source != null)
        {
            UnregisterHotKey(_source.Handle, UndoHotkeyId);
            _undoRegistered = false;
        }
    }

    private void EnsureWindow()
    {
        if (_source != null) return;
        // メッセージ受信専用の不可視ウィンドウ
        var parameters = new HwndSourceParameters("VoiceDockHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        int id = wParam.ToInt32();
        if (id == RecordHotkeyId)
        {
            HotkeyPressed?.Invoke();
            if (PushToTalk) StartReleaseWatch();
            handled = true;
        }
        else if (id == UndoHotkeyId)
        {
            UndoPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>押しっぱなし方式で、キーが離されるのをポーリングで監視する。</summary>
    private void StartReleaseWatch()
    {
        StopReleaseWatch();
        _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _releaseTimer.Tick += (_, _) =>
        {
            // 最上位ビットが立っていなければ、そのキーは離されている
            if ((GetAsyncKeyState((int)_recordVk) & 0x8000) == 0)
            {
                StopReleaseWatch();
                HotkeyReleased?.Invoke();
            }
        };
        _releaseTimer.Start();
    }

    private void StopReleaseWatch()
    {
        _releaseTimer?.Stop();
        _releaseTimer = null;
    }

    public void Dispose()
    {
        Unregister();
        UnregisterUndo();
        _source?.Dispose();
        _source = null;
    }

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
