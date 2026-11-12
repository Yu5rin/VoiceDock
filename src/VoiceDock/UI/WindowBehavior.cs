using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// すべての画面に共通の振る舞いをまとめて付ける。
/// ・タイトルバーをテーマの色にする
/// ・Esc で閉じる
/// ・前回の位置と大きさで開く
/// ・閉じているコンボボックスの上でマウスホイールを回しても、設定が変わらないようにする
///   （画面をスクロールしようとして、気づかないうちに設定を変えてしまうため）
/// </summary>
public static class WindowBehavior
{
    /// <summary>位置と大きさの保存先。起動時に App が設定する。</summary>
    public static SettingsService? Settings { get; set; }

    /// <summary>更新で入れ替えた後など、取りやめられない終了の最中か（閉じる前の確認を出さないため）。</summary>
    public static bool ForceClosing { get; set; }

    /// <param name="key">位置と大きさを覚えるための画面の名前。null なら覚えない。</param>
    /// <param name="closeOnEscape">Esc で閉じるか。</param>
    public static void Attach(Window window, string? key, bool closeOnEscape = true)
    {
        AppTheme.ApplyToWindow(window);

        if (closeOnEscape)
        {
            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                window.Close();
            };
        }

        window.PreviewMouseWheel += OnPreviewMouseWheel;

        if (key == null) return;
        RestorePlacement(window, key);
        window.Closing += (_, e) =>
        {
            // 閉じるのを取りやめた場合（保存の確認でキャンセル）にも呼ばれるが、位置を覚えるだけなので害はない
            SavePlacement(window, key);
        };
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var combo = FindAncestor<ComboBox>(e.OriginalSource as DependencyObject);
        if (combo == null || combo.IsDropDownOpen) return;

        // コンボボックスには渡さず、外側（スクロールする領域）へ回す
        e.Handled = true;
        if (VisualTreeHelper.GetParent(combo) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = combo,
            });
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T found) return found;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static void RestorePlacement(Window window, string key)
    {
        try
        {
            if (Settings?.Current.WindowPlacements.TryGetValue(key, out var text) != true || text == null) return;
            var parts = text.Split(',');
            if (parts.Length != 4) return;
            var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            double left = v[0], top = v[1], width = v[2], height = v[3];
            if (width < window.MinWidth || height < window.MinHeight) return;

            // モニターを外した・解像度を変えた後でも、画面の外に開かないようにする
            double vsLeft = SystemParameters.VirtualScreenLeft, vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth, vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            width = Math.Min(width, SystemParameters.VirtualScreenWidth);
            height = Math.Min(height, SystemParameters.VirtualScreenHeight);
            if (left + 80 > vsRight || top + 40 > vsBottom || left + width - 80 < vsLeft || top < vsTop) return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = left;
            window.Top = top;
            if (window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            {
                window.Width = width;
                window.Height = height;
            }
        }
        catch
        {
            // 覚えていた値が読めなければ、既定の位置で開く
        }
    }

    private static void SavePlacement(Window window, string key)
    {
        try
        {
            if (Settings == null || window.WindowState != WindowState.Normal) return;
            if (double.IsNaN(window.Left) || double.IsNaN(window.Top)) return;
            var text = string.Join(",", new[] { window.Left, window.Top, window.ActualWidth, window.ActualHeight }
                .Select(d => Math.Round(d).ToString(CultureInfo.InvariantCulture)));
            if (Settings.Current.WindowPlacements.TryGetValue(key, out var old) && old == text) return;
            Settings.Update(s => s.WindowPlacements[key] = text);
        }
        catch
        {
            // 覚えられなくても、画面は閉じる
        }
    }
}
