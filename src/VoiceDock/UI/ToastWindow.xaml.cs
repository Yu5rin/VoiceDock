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

    private ToastWindow(string message, ToastKind kind)
    {
        InitializeComponent();
        MessageText.Text = message;
        AccentBar.Background = kind switch
        {
            ToastKind.Error => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
            ToastKind.Warning => new SolidColorBrush(Color.FromRgb(0xE5, 0xA3, 0x3B)),
            _ => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2)),
        };
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

        // 長い文章ほど読む時間が要るため、文字数に応じて表示時間を伸ばす
        var duration = Duration + TimeSpan.FromMilliseconds(Math.Min(4000, MessageText.Text.Length * 60));
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    /// <summary>表示中のトーストを、画面右下から上へ順に積み直す。</summary>
    private static void Restack()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 16;

        // 新しいものが下に来るよう、末尾から積む
        for (int i = Visible.Count - 1; i >= 0; i--)
        {
            var toast = Visible[i];
            if (!toast.IsLoaded) continue;
            toast.Left = area.Right - toast.ActualWidth - 16;
            toast.Top = bottom - toast.ActualHeight;
            bottom -= toast.ActualHeight + 8;
        }
    }

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
