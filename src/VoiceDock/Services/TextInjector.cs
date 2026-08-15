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
    /// 短時間に確定テキストが連続で届いた場合でも、前の入力の送信が完全に終わってから
    /// 次の入力を始めるようにする（日本語 IME との競合による文字化け・順序崩れの対策）。
    /// </summary>
    private static readonly object SendLock = new();

    /// <summary>
    /// フォーカス中のウィンドウにキーボードフォーカスを持つコントロールがあるかを判定する。
    /// 無い（デスクトップ等）の場合は流し込みを行わない。
    /// </summary>
    public static bool HasTextInputFocus() => GetFocusedControl() != IntPtr.Zero;

    /// <summary>
    /// 前面ウィンドウの中でキーボードフォーカスを持つコントロールのハンドルを取得する。
    /// 無い（デスクトップ等）場合は IntPtr.Zero。
    /// </summary>
    private static IntPtr GetFocusedControl()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        uint threadId = GetWindowThreadProcessId(hwnd, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return IntPtr.Zero;

        return info.hwndFocus;
    }

    /// <summary>
    /// クリップボード経由でテキストを貼り付ける (Ctrl+V)。
    /// 直接キー入力を受け付けないアプリ向けの代替方式。元のクリップボード内容は復元する。
    /// UI (STA) スレッドから呼ぶこと。
    /// </summary>
    public static bool SendViaClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        // SendText と同じロックで直列化し、確定テキストが連続で届いても
        // クリップボードの内容が競合しないようにする。
        lock (SendLock)
        {
            return SendViaClipboardCore(text);
        }
    }

    private static bool SendViaClipboardCore(string text)
    {
        if (!HasTextInputFocus()) return false;

        string? backup = null;
        bool hadText = false;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                backup = System.Windows.Clipboard.GetText();
                hadText = true;
            }
            System.Windows.Clipboard.SetDataObject(text, true);

            // Ctrl+V を送出
            var inputs = new[]
            {
                MakeKeyInput(VK_CONTROL, keyUp: false),
                MakeKeyInput(VK_V, keyUp: false),
                MakeKeyInput(VK_V, keyUp: true),
                MakeKeyInput(VK_CONTROL, keyUp: true),
            };
            bool ok = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

            // 貼り付け処理がクリップボードを読み終えるのを待ってから復元する
            if (hadText)
            {
                var restore = backup;
                _ = Task.Delay(300).ContinueWith(_ =>
                {
                    try
                    {
                        var thread = new Thread(() =>
                        {
                            try { System.Windows.Clipboard.SetDataObject(restore!, true); } catch { /* 復元失敗は無視 */ }
                        });
                        thread.SetApartmentState(ApartmentState.STA);
                        thread.Start();
                    }
                    catch { /* 復元失敗は無視 */ }
                });
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        }
    };

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>テキストをキー入力として送出する。成功可否を返す。</summary>
    public static bool SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        // 確定テキストが短時間に連続で届いても、前の送信が完全に終わってから
        // 次を送る（直列化）。IME 経由の非同期処理と競合すると、文字の順序が
        // 入れ替わったり別々の確定結果が混ざったりするため。
        lock (SendLock)
        {
            var focused = GetFocusedControl();
            if (focused == IntPtr.Zero) return false;

            var inputs = new List<INPUT>(text.Length * 2);
            foreach (char c in text)
            {
                // 改行は Enter キーとしてではなく LF のユニコード入力として送ると
                // 受け側で無視されることがあるため CR に正規化する
                char ch = c == '\n' ? '\r' : c;
                inputs.Add(MakeUnicodeInput(ch, keyUp: false));
                inputs.Add(MakeUnicodeInput(ch, keyUp: true));
            }

            // 送信中だけ対象コントロールの IME コンテキストを一時的に外す。
            // 日本語 IME がオンのまま SendInput の Unicode イベントを送ると、
            // IME の非同期な変換・確定処理を経由してしまい、複数フレーズを
            // 連続入力した際に文字の順序が入れ替わることがあるための対策。
            IntPtr prevHimc = ImmAssociateContext(focused, IntPtr.Zero);
            try
            {
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

                // 送信したキーイベントが対象アプリ側で処理し終わるまで待つ（同期バリア）。
                // これを待たずに次のフレーズを送ると、前の入力の処理中に割り込んでしまう。
                SendMessageTimeout(focused, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                    SMTO_NORMAL | SMTO_ABORTIFHUNG, 500, out _);
                return true;
            }
            finally
            {
                // IME コンテキストを元に戻す
                ImmAssociateContext(focused, prevHimc);
            }
        }
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

    private const uint WM_NULL = 0x0000;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// 対象ウィンドウのメッセージキューが捌けるまで（同期的に）待つための WM_NULL 送信。
    /// SendInput で積んだキーイベントの処理完了バリアとして使う。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// 指定コントロールの IME コンテキストを差し替える。戻り値は差し替え前の HIMC で、
    /// 元に戻す際に再度この関数へ渡す。IntPtr.Zero を渡すと IME を一時的に無効化できる。
    /// </summary>
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);
}
