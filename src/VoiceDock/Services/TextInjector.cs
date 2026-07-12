using System.Runtime.InteropServices;

namespace VoiceDock.Services;

/// <summary>
/// 認識結果テキストを、フォーカスされているテキスト入力欄へ
/// SendInput (KEYEVENTF_UNICODE) による直接キー入力として流し込む。
/// クリップボードは使用しない。入力欄が無い場合は何もしない（エラー通知なし）。
/// </summary>
public static class TextInjector
{
    /// <summary>
    /// フォーカス中のウィンドウにキーボードフォーカスを持つコントロールがあるかを判定する。
    /// 無い（デスクトップ等）の場合は流し込みを行わない。
    /// </summary>
    public static bool HasTextInputFocus()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        uint threadId = GetWindowThreadProcessId(hwnd, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return false;

        return info.hwndFocus != IntPtr.Zero;
    }

    /// <summary>テキストをキー入力として送出する。成功可否を返す。</summary>
    public static bool SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (!HasTextInputFocus()) return false;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            // 改行は Enter キーとしてではなく LF のユニコード入力として送ると
            // 受け側で無視されることがあるため CR に正規化する
            char ch = c == '\n' ? '\r' : c;
            inputs.Add(MakeUnicodeInput(ch, keyUp: false));
            inputs.Add(MakeUnicodeInput(ch, keyUp: true));
        }

        // 一括送信でイベント間への割り込みを防ぐ（大きすぎる場合は分割）
        const int chunkSize = 512;
        var array = inputs.ToArray();
        for (int offset = 0; offset < array.Length; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, array.Length - offset);
            var chunk = new INPUT[count];
            Array.Copy(array, offset, chunk, 0, count);
            if (SendInput((uint)count, chunk, Marshal.SizeOf<INPUT>()) != count)
                return false;
        }
        return true;
    }

    private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        }
    };

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
