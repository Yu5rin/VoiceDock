using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VoiceDock.UI;

public enum ToastKind { Info, Warning, Error }

/// <summary>
/// アプリ固有のトースト風ポップアップ（Windows 標準のバルーン通知は使用しない）。
/// 画面右下に数秒表示してフェードアウトする。
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    /// <summary>同時に出す上限。これを超えたら古いものから閉じる。</summary>
    private const int MaxVisible = 4;

    /// <summary>表示中のトースト。同じ位置に重ねないよう、下から順に積むために保持する。</summary>
    private static readonly List<ToastWindow> Visible = new();

    private readonly ToastKind _kind;

    private ToastWindow(string message, ToastKind kind)
    {
        InitializeComponent();
        _kind = kind;
        MessageText.Text = message;
        AccentBar.Background = kind switch
        {
            ToastKind.Error => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            ToastKind.Warning => new SolidColorBrush(Color.FromRgb(0xE5, 0xA3, 0x3B)),
            _ => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
        };

        // 大事な知らせは、目を離している間に消えてしまわないようにする
        if (kind == ToastKind.Error)
        {
            TitleText.Text = "VoiceDock — 確認してください";
            DismissHint.Visibility = Visibility.Visible;
        }

        // どの通知もクリックで閉じられるようにする
        MouseLeftButtonDown += (_, _) => FadeOutAndClose();

        SourceInitialized += (_, _) => ApplyNoActivateStyle();
    }

    /// <summary>トーストを表示する。UI スレッド以外から呼んでもよい。</summary>
    public static void Show(string message, ToastKind kind = ToastKind.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            var toast = new ToastWindow(message, kind);
            toast.ShowToast();
        });
    }

    private void ShowToast()
    {
        // 同時に出しすぎると読めないため、古いものから閉じる
        while (Visible.Count >= MaxVisible)
        {
            var oldest = Visible[0];
            Visible.RemoveAt(0);
            try { oldest.Close(); } catch { /* 既に閉じている場合は無視 */ }
        }

        Show();
        UpdateLayout();
        Visible.Add(this);
        Closed += (_, _) =>
        {
            Visible.Remove(this);
            Restack();
        };

        Restack();

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });

        // エラーは自動で消さない。見落とすと原因が分からないまま使い続けることになる
        if (_kind == ToastKind.Error) return;

        // 長い文章ほど読む時間が要るため、文字数に応じて表示時間を伸ばす
        var duration = Duration + TimeSpan.FromMilliseconds(Math.Min(4000, MessageText.Text.Length * 60));
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOutAndClose();
        };
        timer.Start();
    }

    private void FadeOutAndClose()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) =>
        {
            try { Close(); } catch { /* 既に閉じている場合は無視 */ }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// 表示中のトーストを、作業中のモニターの右下から上へ順に積み直す。
    ///
    /// SystemParameters.WorkArea は主モニターの領域しか返さないため、
    /// マルチモニターでは別の画面で作業していても主モニターに出てしまう。
    /// オーバーレイと同じく、前面ウィンドウ（無ければマウスカーソル）のある
    /// モニターへ出す。
    /// </summary>
    private static void Restack()
    {
        var area = GetTargetWorkingArea(out var anchor);
        double scale = GetScaleForPoint(anchor.X, anchor.Y);
        int margin = (int)Math.Round(16 * scale);
        int gap = (int)Math.Round(8 * scale);
        int bottom = area.Bottom - margin;

        // 新しいものが下に来るよう、末尾から積む
        for (int i = Visible.Count - 1; i >= 0; i--)
        {
            var toast = Visible[i];
            if (!toast.IsLoaded) continue;

            int widthPx = (int)Math.Round(toast.ActualWidth * scale);
            int heightPx = (int)Math.Round(toast.ActualHeight * scale);
            int x = area.Right - widthPx - margin;
            int y = bottom - heightPx;

            var hwnd = new WindowInteropHelper(toast).Handle;
            if (hwnd == IntPtr.Zero) continue;
            // 大きさは内容にあわせて WPF が決めるため、位置だけを動かす
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);

            bottom -= heightPx + gap;
        }
    }

    /// <summary>前面ウィンドウ（無ければカーソル）のあるモニターの作業領域をピクセルで返す。</summary>
    private static System.Drawing.Rectangle GetTargetWorkingArea(out System.Drawing.Point anchor)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            anchor = new System.Drawing.Point((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
            return System.Windows.Forms.Screen.FromPoint(anchor).WorkingArea;
        }
        anchor = System.Windows.Forms.Cursor.Position;
        return System.Windows.Forms.Screen.FromPoint(anchor).WorkingArea;
    }

    /// <summary>モニターごとの拡大率（WPF 単位 → ピクセル）。</summary>
    private static double GetScaleForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new POINT { x = x, y = y }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
                return dpiX / 96.0;
        }
        catch
        {
            // shcore が無い環境ではシステム DPI にフォールバック
        }
        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private void ApplyNoActivateStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
