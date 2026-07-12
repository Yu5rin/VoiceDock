using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// グローバルホットキーの登録。RegisterHotKey を用い、
/// 登録失敗（他アプリとの衝突）を検出して呼び出し元へ返す。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xB00C;
    private const int WmHotkey = 0x0312;

    private HwndSource? _source;
    private bool _registered;

    public event Action? HotkeyPressed;

    /// <summary>
    /// ホットキーを登録する。既存の登録は解除される。
    /// 他アプリと衝突している場合は false を返す（登録は行われない）。
    /// </summary>
    public bool TryRegister(HotkeySpec spec)
    {
        EnsureWindow();
        Unregister();

        uint modifiers = MOD_NOREPEAT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (spec.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (spec.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(spec.Key);

        _registered = RegisterHotKey(_source!.Handle, HotkeyId, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source != null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
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
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
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
}
