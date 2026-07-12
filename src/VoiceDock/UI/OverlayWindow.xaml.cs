using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VoiceDock.UI;

/// <summary>
/// 録音中オーバーレイ。マウスカーソルがあるモニターの下部中央に表示し、
/// マイクアイコンと音量に応じて動く波形（赤系一色）を描画する。
/// フェードイン/フェードアウトで表示を切り替え、クリックは透過する。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int BarCount = 36;
    private const double BarWidth = 3;
    private const double BarGap = 3;

    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly DispatcherTimer _renderTimer;
    private volatile float _currentLevel;
    private bool _visible;

    public OverlayWindow()
    {
        InitializeComponent();

        for (int i = 0; i < BarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = BarBrush,
            };
            _bars[i] = bar;
            WaveCanvas.Children.Add(bar);
        }

        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _renderTimer.Tick += (_, _) => RenderWave();

        SourceInitialized += (_, _) => ApplyClickThroughStyles();
    }

    /// <summary>録音レベル (RMS 0..1) を反映する。どのスレッドから呼んでもよい。</summary>
    public void UpdateLevel(float rms) => _currentLevel = rms;

    public void ShowOverlay()
    {
        if (_visible) return;
        _visible = true;

        PositionToCursorMonitor();
        Show();
        _renderTimer.Start();

        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
    }

    public void HideOverlay()
    {
        if (!_visible) return;
        _visible = false;
        _renderTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase(),
        };
        fadeOut.Completed += (_, _) =>
        {
            if (!_visible)
            {
                Hide();
                Array.Clear(_levels);
                _currentLevel = 0;
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void RenderWave()
    {
        // 最新レベルを右端に積み、左へ流す
        Array.Copy(_levels, 1, _levels, 0, BarCount - 1);
        // RMS は小さい値になりがちなので視認しやすい値へ増幅
        _levels[BarCount - 1] = Math.Clamp(_currentLevel * 9.0, 0.04, 1.0);

        double canvasH = WaveCanvas.ActualHeight;
        double canvasW = WaveCanvas.ActualWidth;
        double totalW = BarCount * (BarWidth + BarGap) - BarGap;
        double left = Math.Max(0, (canvasW - totalW) / 2);

        for (int i = 0; i < BarCount; i++)
        {
            double h = Math.Max(3, _levels[i] * canvasH);
            _bars[i].Height = h;
            System.Windows.Controls.Canvas.SetLeft(_bars[i], left + i * (BarWidth + BarGap));
            System.Windows.Controls.Canvas.SetTop(_bars[i], (canvasH - h) / 2);
        }
    }

    /// <summary>マウスカーソルが存在するモニターの下部中央（ピクセル座標）に配置する。</summary>
    private void PositionToCursorMonitor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;

        // モニターごとの DPI で WPF 単位 → ピクセルに換算する
        double scale = GetScaleForPoint(cursor.X, cursor.Y);
        int widthPx = (int)Math.Round(Width * scale);
        int heightPx = (int)Math.Round(Height * scale);
        int x = area.Left + (area.Width - widthPx) / 2;
        int y = area.Bottom - heightPx - (int)Math.Round(24 * scale);

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, widthPx, heightPx, SWP_NOZORDER | SWP_NOACTIVATE);
    }

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

    /// <summary>クリック透過・非アクティブ化・Alt+Tab 非表示のウィンドウスタイルを適用する。</summary>
    private void ApplyClickThroughStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
