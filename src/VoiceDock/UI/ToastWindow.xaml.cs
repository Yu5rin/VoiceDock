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
        Show();
        UpdateLayout();

        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });

        var timer = new DispatcherTimer { Interval = Duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
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
