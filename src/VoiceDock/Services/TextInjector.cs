using System.Linq;
using System.Runtime.InteropServices;
using VoiceDock.Models;

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
    /// 前面ウィンドウを持つプロセスの名前（拡張子なし）を取得する。
    /// 取得できない場合は空文字。アプリごとの入力方式の切り替えに使う。
    /// </summary>
    public static string GetForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>前面ウィンドウのハンドル（無ければ IntPtr.Zero）。</summary>
    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    /// <summary>
    /// 指定したウィンドウを前面に出す。最小化されていれば元に戻す。
    /// ウィンドウが既に無い場合は false。
    /// </summary>
    public static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        return SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// 直前に入力した文字数ぶん BackSpace を送って取り消す。
    /// 対象アプリが入力を受け付けない場合は false。
    /// </summary>
    public static bool SendBackspaces(int count)
    {
        if (count <= 0) return true;

        lock (SendLock)
        {
            var focused = GetFocusedControl();
            if (focused == IntPtr.Zero) return false;

            var inputs = new List<INPUT>(count * 2);
            for (int i = 0; i < count; i++)
            {
                inputs.Add(MakeKeyInput(VK_BACK, keyUp: false));
                inputs.Add(MakeKeyInput(VK_BACK, keyUp: true));
            }

            var array = inputs.ToArray();
            var heldModifiers = ReleaseHeldModifiers();
            try
            {
                const int chunkSize = 512;
                for (int offset = 0; offset < array.Length; offset += chunkSize)
                {
                    int n = Math.Min(chunkSize, array.Length - offset);
                    var chunk = new INPUT[n];
                    Array.Copy(array, offset, chunk, 0, n);
                    if (SendInput((uint)n, chunk, Marshal.SizeOf<INPUT>()) != n)
                        return false;
                }
                return true;
            }
            finally
            {
                RestoreHeldModifiers(heldModifiers);
            }
        }
    }

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

        // 文字列だけを控えていると、画像やファイルをコピーしていた場合に
        // 中身を消してしまう。形式を問わず控えを取っておく。
        var backup = CaptureClipboard();
        List<ushort>? heldModifiers = null;
        try
        {
            System.Windows.Clipboard.SetDataObject(text, true);

            // ホットキーを握ったままだと Ctrl+Shift+V 等になってしまうため、いったん離す
            heldModifiers = ReleaseHeldModifiers();

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
            if (backup != null) RestoreClipboardLater(backup);
            return ok;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (heldModifiers != null) RestoreHeldModifiers(heldModifiers);
        }
    }

    /// <summary>
    /// 現在のクリップボードの内容を、形式を保ったまま控える。
    /// 取得できない形式は諦めるが、少なくとも文字列・画像・ファイル一覧は残せる。
    /// </summary>
    private static System.Windows.DataObject? CaptureClipboard()
    {
        try
        {
            var current = System.Windows.Clipboard.GetDataObject();
            if (current == null) return null;

            var copy = new System.Windows.DataObject();
            bool any = false;
            foreach (var format in current.GetFormats())
            {
                try
                {
                    var data = current.GetData(format);
                    if (data == null) continue;
                    copy.SetData(format, data);
                    any = true;
                }
                catch
                {
                    // 取り出せない形式は諦める
                }
            }
            return any ? copy : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>貼り付けが終わる頃合いを見て、控えたクリップボードの内容を書き戻す。</summary>
    private static void RestoreClipboardLater(System.Windows.DataObject backup)
    {
        _ = Task.Delay(300).ContinueWith(_ =>
        {
            try
            {
                var thread = new Thread(() =>
                {
                    try { System.Windows.Clipboard.SetDataObject(backup, true); } catch { /* 復元失敗は無視 */ }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch { /* 復元失敗は無視 */ }
        });
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

    /// <summary>改行のキーイベントを組み立てる（修飾キー + Enter）。</summary>
    private static void AppendNewlineKeys(List<INPUT> inputs, NewlineMode mode)
    {
        ushort? modifier = mode switch
        {
            NewlineMode.ShiftEnter => VK_SHIFT,
            NewlineMode.AltEnter => VK_MENU,
            _ => null,
        };

        if (modifier is { } mod) inputs.Add(MakeKeyInput(mod, keyUp: false));
        inputs.Add(MakeKeyInput(VK_RETURN, keyUp: false));
        inputs.Add(MakeKeyInput(VK_RETURN, keyUp: true));
        if (modifier is { } mod2) inputs.Add(MakeKeyInput(mod2, keyUp: true));
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_MENU = 0x12;   // Alt
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    /// <summary>入力の邪魔になる修飾キー。押しっぱなし方式ではホットキーを握ったまま送出されるため。</summary>
    private static readonly ushort[] ModifierKeysToRelease =
        { VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN };

    /// <summary>
    /// 利用者が物理的に押している修飾キーを、一時的に「離した」ことにする。
    ///
    /// 押している間だけ録音する方式では、Ctrl+Shift+... を握ったまま入力が始まる。
    /// その状態で文字や Ctrl+V を送ると、対象アプリはショートカット（Ctrl+Shift+V 等）
    /// として解釈してしまい、文字が入らなかったり別の動作をしたりする。
    /// </summary>
    /// <returns>解除したキーの一覧（あとで押し直すために使う）。</returns>
    private static List<ushort> ReleaseHeldModifiers()
    {
        var released = new List<ushort>();
        foreach (var vk in ModifierKeysToRelease)
        {
            // 最上位ビットが立っていれば、そのキーは今押されている
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
            released.Add(vk);
        }
        if (released.Count == 0) return released;

        var inputs = released.Select(vk => MakeKeyInput(vk, keyUp: true)).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return released;
    }

    /// <summary>解除した修飾キーを、まだ押されたままなら押し直す。</summary>
    private static void RestoreHeldModifiers(List<ushort> released)
    {
        if (released.Count == 0) return;
        var stillHeld = released.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0)
                                .Select(vk => MakeKeyInput(vk, keyUp: false))
                                .ToArray();
        if (stillHeld.Length > 0)
            SendInput((uint)stillHeld.Length, stillHeld, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// テキストをキー入力として送出する。成功可否を返す。
    /// </summary>
    /// <param name="newlineMode">
    /// 改行をどのキー操作として送るか。Shift+Enter は多くのアプリで改行になり、
    /// Enter が「送信」のチャットアプリでも誤送信しないため既定にしている。
    /// </param>
    public static bool SendText(string text, NewlineMode newlineMode = NewlineMode.ShiftEnter)
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
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '\n' or '\r')
                {
                    // 改行は Unicode 文字ではなく実際の Enter キーとして送る。
                    // Unicode の CR を送るとチャットアプリでは「送信」と解釈されてしまい、
                    // かつ改行を受け付けない入力欄では何も起きないことがあるため。
                    AppendNewlineKeys(inputs, newlineMode);
                    // CRLF は 2 文字で 1 つの改行。片方を読み飛ばさないと 2 行空いてしまう
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    continue;
                }
                inputs.Add(MakeUnicodeInput(c, keyUp: false));
                inputs.Add(MakeUnicodeInput(c, keyUp: true));
            }

            // 入力先の IME をオフにする（戻すのは音声入力が終わってから）。
            //
            // 日本語 IME がオンのまま SendInput の Unicode イベントを送ると、文字が
            // IME の変換バッファ（未確定文字列）に吸い込まれ、順序が入れ替わったり
            // 欠けたりする。
            //
            // 以前は 1 回送るごとに IME をオフ → 送信 → 文字数ミリ秒待って戻す、としていた。
            // しかし Chrome や Electron 製のアプリは送ったキーを読み終えるのに時間がかかり、
            // 読み終える前に IME が戻ると、残りの文字が IME を通ってしまっていた。
            // 相手がいつ読み終えたかを知る確実な手段は無い（SendMessage は入力キューを
            // 追い越すため待ち合わせに使えない）ため、音声入力中はずっとオフのままにし、
            // 止めてから十分な間を置いて <see cref="RestoreSuppressedIme"/> で戻す。
            //
            // IME 状態の変更は ImmAssociateContext ではなく、対象スレッドの既定 IME
            // ウィンドウへ WM_IME_CONTROL を送る方式を使う。ImmAssociateContext は
            // 他プロセスのウィンドウには効かないため。
            SuppressIme(focused);

            // ホットキーを握ったままでもそのまま文字が入るようにする
            var heldModifiers = ReleaseHeldModifiers();
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
                return true;
            }
            finally
            {
                RestoreHeldModifiers(heldModifiers);
            }
        }
    }

    /// <summary>
    /// キー操作（Enter、Ctrl+A など）を前面の入力欄へ送る。
    /// 入力欄が無い場合は何もせず false。
    /// </summary>
    public static bool SendKeyStroke(KeyStroke key)
    {
        lock (SendLock)
        {
            var focused = GetFocusedControl();
            if (focused == IntPtr.Zero) return false;

            // IME がオンのままだと、Enter が「変換の確定」に使われて送信されない等が起きるため、
            // 文字の入力と同じく IME をオフにしてから送る
            SuppressIme(focused);
            var heldModifiers = ReleaseHeldModifiers();
            try
            {
                var modifiers = new List<ushort>();
                if (key.Ctrl) modifiers.Add(VK_CONTROL);
                if (key.Shift) modifiers.Add(VK_SHIFT);
                if (key.Alt) modifiers.Add(VK_MENU);

                var inputs = new List<INPUT>();
                foreach (var m in modifiers) inputs.Add(MakeKeyInput(m, keyUp: false));
                inputs.Add(MakeKeyInput(key.VirtualKey, keyUp: false));
                inputs.Add(MakeKeyInput(key.VirtualKey, keyUp: true));
                for (int i = modifiers.Count - 1; i >= 0; i--) inputs.Add(MakeKeyInput(modifiers[i], keyUp: true));

                var array = inputs.ToArray();
                return SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>()) == array.Length;
            }
            finally
            {
                RestoreHeldModifiers(heldModifiers);
            }
        }
    }

    /// <summary>
    /// 前面の入力欄の IME がオンかどうか（動作チェック画面で表示する）。
    /// 入力欄が無い・IME が無い場合は null。
    /// </summary>
    public static bool? GetForegroundImeOpen()
    {
        try
        {
            var focused = GetFocusedControl();
            if (focused == IntPtr.Zero) return null;
            IntPtr imeWnd = ImmGetDefaultIMEWnd(focused);
            if (imeWnd == IntPtr.Zero) return null;
            return SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero) != IntPtr.Zero;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>音声入力のあいだ IME をオフにしている入力先（既定 IME ウィンドウ）。</summary>
    private static readonly HashSet<IntPtr> SuppressedImeWindows = new();

    /// <summary>入力先の IME がオンなら、オフにして覚えておく。SendLock の中で呼ぶこと。</summary>
    private static void SuppressIme(IntPtr focused)
    {
        try
        {
            // 対象スレッドの既定 IME ウィンドウ。ウィンドウメッセージ経由なので
            // 他プロセスのウィンドウに対しても機能する。
            IntPtr imeWnd = ImmGetDefaultIMEWnd(focused);
            if (imeWnd == IntPtr.Zero) return;

            bool open = SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_GETOPENSTATUS, IntPtr.Zero) != IntPtr.Zero;
            if (!open) return;   // 元々オフ（または既にオフにしてある）なら何もしない

            SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, IntPtr.Zero);
            SuppressedImeWindows.Add(imeWnd);
        }
        catch
        {
            // IME が利用できない環境では何もしない（通常の入力にフォールバック）
        }
    }

    /// <summary>
    /// 音声入力のためにオフにした IME を、すべて元のオンに戻す。
    /// 送った文字を相手が読み終えてから呼ぶこと（呼び出し側で十分な間を置く）。
    /// </summary>
    public static void RestoreSuppressedIme()
    {
        lock (SendLock)
        {
            foreach (var imeWnd in SuppressedImeWindows)
            {
                try
                {
                    if (IsWindow(imeWnd))
                        SendMessage(imeWnd, WM_IME_CONTROL, (IntPtr)IMC_SETOPENSTATUS, (IntPtr)1);
                }
                catch
                {
                    // 相手のウィンドウが閉じられている等。戻せなくても害は無い
                }
            }
            SuppressedImeWindows.Clear();
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
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IMC_SETOPENSTATUS = 0x0006;

    /// <summary>指定ウィンドウを持つスレッドの既定 IME ウィンドウを取得する。</summary>
    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
