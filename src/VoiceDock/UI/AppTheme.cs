using System.Windows;
using System.Windows.Interop;
using VoiceDock.Services;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace VoiceDock.UI;

/// <summary>
/// アプリのテーマ（現状はダーク固定）を集約し、ウィンドウのタイトルバー配色を
/// TitleBarThemeHelper 経由で連動させる。
/// 現状 VoiceDock は Theme.xaml のとおりダークテーマ固定だが、将来ライトテーマや
/// アクセントカラー選択を追加した際は <see cref="SetDarkMode"/> のようなメソッドを追加し
/// <see cref="Changed"/> を発火させれば、開いている全ウィンドウのタイトルバーへ即座に伝播する。
/// </summary>
public static class AppTheme
{
    /// <summary>現在ダークテーマかどうか。</summary>
    public static bool IsDarkMode { get; private set; } = true;

    /// <summary>テーマが変化したときに発火する（将来のライト/アクセントカラー切替用）。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 指定ウィンドウのタイトルバーへ現在のテーマ配色を適用する。
    /// ネイティブウィンドウハンドルが必要なため、生成直後（未初期化）でも安全に呼べるよう
    /// SourceInitialized のタイミングまで自動的に遅延させる。
    /// </summary>
    public static void ApplyToWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
        {
            // ハンドルがまだ無い（Show() 前）場合は、生成され次第 1 回だけ適用する
            void OnSourceInitialized(object? s, EventArgs e)
            {
                window.SourceInitialized -= OnSourceInitialized;
                ApplyNow(window);
            }
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        ApplyNow(window);
    }

    private static void ApplyNow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        TitleBarThemeHelper.Apply(hwnd, IsDarkMode, ToDrawingColor(CaptionColor), ToDrawingColor(TextColor));
    }

    /// <summary>タイトルバーの背景色。既定はアプリ本体と同じ背景色（Theme.xaml の BgColor）。</summary>
    private static MediaColor CaptionColor =>
        (MediaColor)Application.Current.Resources["BgColor"];

    /// <summary>タイトルバーの文字色。既定はアプリ本体と同じ前景色（Theme.xaml の FgColor）。</summary>
    private static MediaColor TextColor =>
        (MediaColor)Application.Current.Resources["FgColor"];

    private static DrawingColor ToDrawingColor(MediaColor c) => DrawingColor.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>
    /// 開いているすべてのウィンドウのタイトルバーへ現在のテーマを再適用する。
    /// テーマ切替 UI を実装した際は、切替直後にこれを呼べば全ウィンドウへ即時反映できる。
    /// </summary>
    public static void ApplyToAllOpenWindows()
    {
        foreach (Window window in Application.Current.Windows)
            ApplyToWindow(window);
    }

    /// <summary>
    /// ダーク/ライトを切り替える（将来、設定画面にテーマ切替 UI を追加した際の呼び出し口）。
    /// 現状 VoiceDock はダークテーマ固定のため未使用だが、切替を実装した際は
    /// ここでライト配色への切り替えも行い、Changed を発火させる。
    /// </summary>
    public static void SetDarkMode(bool isDarkMode)
    {
        if (IsDarkMode == isDarkMode) return;
        IsDarkMode = isDarkMode;
        ApplyToAllOpenWindows();
        Changed?.Invoke();
    }
}
