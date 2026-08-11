using System.Drawing;
using System.Runtime.InteropServices;

namespace VoiceDock.Services;

/// <summary>
/// Windows 11 の DWM API を使い、ウィンドウのタイトルバー（キャプション）の
/// 背景色・文字色・ダークモードをアプリのテーマに合わせて設定する。
/// WinForms/WPF どちらでもウィンドウハンドル (IntPtr) さえ渡せば動作する。
/// Windows 10 以下や、何らかの理由で DWM 呼び出しが失敗する環境では何もしない
/// （例外を投げず、OS 標準のタイトルバーのまま）。
/// </summary>
public static class TitleBarThemeHelper
{
    // Windows 11 (21H2) 以降で追加された DWMWINDOWATTRIBUTE の値。
    // 古い SDK ヘッダーにはまだ定義がないため、値を直接指定する。
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>Windows 11（ビルド 22000 以降）でのみ DWM のタイトルバー装飾 API が有効。</summary>
    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// タイトルバーの配色をテーマに合わせて設定する。
    /// Windows 11 未満の環境や DWM 呼び出しに失敗した場合は、何もせず静かに戻る
    /// （呼び出し側でのバージョン分岐やエラーハンドリングは不要）。
    /// </summary>
    /// <param name="hwnd">対象ウィンドウのハンドル（WinForms: this.Handle / WPF: new WindowInteropHelper(this).Handle）</param>
    /// <param name="isDarkMode">ダークテーマかどうか。true の場合システムメニュー等もダーク表示にする</param>
    /// <param name="captionColor">タイトルバーの背景色</param>
    /// <param name="textColor">タイトルバーの文字色</param>
    public static void Apply(IntPtr hwnd, bool isDarkMode, Color captionColor, Color textColor)
    {
        if (hwnd == IntPtr.Zero || !IsWindows11) return;

        try
        {
            int darkMode = isDarkMode ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int caption = ToColorRef(captionColor);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

            int text = ToColorRef(textColor);
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch
        {
            // DWM が利用できない環境（未対応 OS ビルド、リモートデスクトップ制限等）では
            // 何もせず OS 標準のタイトルバーのままにする。
        }
    }

    /// <summary>GDI+ の Color を DWM が要求する COLORREF (0x00BBGGRR) 形式に変換する。</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
