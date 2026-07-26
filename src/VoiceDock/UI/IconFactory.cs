using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VoiceDock.UI;

public enum TrayState { Idle, Recording, Error }

/// <summary>
/// タスクトレイ用アイコンを実行時に描画生成する。
/// 待機中: モノクロ / 録音中: アクセントカラー(赤系) / エラー: 警告色(オレンジ系)。
/// 図形は exe のアプリアイコン (app.ico) と同一意匠。変更時は tools/generate_app_icon.py
/// の座標も合わせて更新すること。
/// </summary>
public static class IconFactory
{
    private static readonly Dictionary<TrayState, Icon> Cache = new();

    public static Icon Get(TrayState state)
    {
        if (Cache.TryGetValue(state, out var cached)) return cached;

        var color = state switch
        {
            TrayState.Recording => Color.FromArgb(229, 72, 77),   // 赤系アクセント
            TrayState.Error => Color.FromArgb(229, 163, 59),      // 黄・オレンジ系警告色
            _ => Color.FromArgb(200, 200, 205),                    // モノクロ
        };

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // マイク本体（カプセル形）
            using (var body = new GraphicsPath())
            {
                var rect = new RectangleF(11, 3, 10, 16);
                body.AddArc(rect.X, rect.Y, rect.Width, rect.Width, 180, 180);
                body.AddArc(rect.X, rect.Bottom - rect.Width, rect.Width, rect.Width, 0, 180);
                body.CloseFigure();
                g.FillPath(brush, body);
            }

            // マイクスタンド（アーチ＋支柱＋台座）
            g.DrawArc(pen, 7f, 8f, 18f, 16f, 20f, 140f);
            g.DrawLine(pen, 16f, 24f, 16f, 28f);
            g.DrawLine(pen, 11f, 28f, 21f, 28f);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            // Icon.FromHandle はハンドルを所有しないため複製してから解放する
            using var tmp = Icon.FromHandle(hIcon);
            var icon = (Icon)tmp.Clone();
            Cache[state] = icon;
            return icon;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
