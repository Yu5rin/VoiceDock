using System.Runtime.InteropServices;
using System.Text;

namespace VoiceDock.Services;

/// <summary>
/// IME 変換中（未確定文字列がある状態）のホットキー無視判定。
/// 他プロセスの未確定文字列を直接取得する公開 API は無いため、
/// フォーカス中スレッドの IME 候補ウィンドウ・変換ウィンドウの表示状態から
/// ベストエフォートで「変換中」を推定する。
/// </summary>
public static class ImeGuard
{
    /// <summary>IME が変換中（未確定文字列あり）と推定される場合 true。</summary>
    public static bool IsImeComposing()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            uint threadId = GetWindowThreadProcessId(foreground, out _);

            // メニュー操作中なども誤爆を避けるため無視扱いにする
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(threadId, ref info))
            {
                const int GUI_INMENUMODE = 0x0004;
                const int GUI_POPUPMENUMODE = 0x0010;
                const int GUI_SYSTEMMENUMODE = 0x0008;
                if ((info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE | GUI_SYSTEMMENUMODE)) != 0)
                    return true;
            }

            // フォーカス中スレッドに表示中の IME 候補/変換ウィンドウがあれば変換中とみなす
            bool composing = false;
            EnumThreadWindows(threadId, (hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var sb = new StringBuilder(256);
                GetClassName(hwnd, sb, sb.Capacity);
                var cls = sb.ToString();
                if (cls.Contains("Candidate", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("Composition", StringComparison.OrdinalIgnoreCase) ||
                    cls == "mscandui31" || cls == "MSCTFIME Composition")
                {
                    composing = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return composing;
        }
        catch
        {
            // 判定に失敗した場合はホットキーを止めない
            return false;
        }
    }

    private delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWndProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
