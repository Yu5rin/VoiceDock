using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceDock.UI;

public enum MessageDialogKind
{
    /// <summary>お知らせ</summary>
    Info,
    /// <summary>注意して選んでほしい確認</summary>
    Warning,
    /// <summary>消す・置き換えるなど、取り消せない操作の確認</summary>
    Danger,
}

public enum MessageDialogResult { Primary, Secondary, Cancel }

/// <summary>
/// 確認やお知らせのダイアログ。
/// Windows 標準のメッセージボックスは白い画面で、ダークテーマの中で浮いて見えるうえ、
/// ボタンが「OK／キャンセル」「はい／いいえ」に限られ、何が起きるのかがボタンから読み取れない。
/// テーマに合わせ、ボタンには「削除する」「保存して閉じる」のように、押すと起きることを書く。
/// </summary>
public static class MessageDialog
{
    /// <summary>
    /// 確認を出す。主ボタンを押したら true。
    /// <paramref name="cancelText"/> が null ならボタンは 1 つ（お知らせ）。
    /// </summary>
    public static bool Show(Window? owner, string title, string message, MessageDialogKind kind,
        string primaryText = "OK", string? cancelText = null) =>
        ShowChoice(owner, title, message, kind, primaryText, null, cancelText) == MessageDialogResult.Primary;

    /// <summary>
    /// 3 つまでの選択肢を出す（例:「保存して閉じる」「保存せずに閉じる」「キャンセル」）。
    /// Esc や × で閉じたときは Cancel。
    /// </summary>
    public static MessageDialogResult ShowChoice(Window? owner, string title, string message, MessageDialogKind kind,
        string primaryText, string? secondaryText, string? cancelText)
    {
        var result = MessageDialogResult.Cancel;
        var window = new Window
        {
            Title = title,
            Style = (Style)Application.Current.FindResource("WindowStyle"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            MinWidth = 380,
            MaxWidth = 560,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner is { IsLoaded: true }) window.Owner = owner;
        AppTheme.ApplyToWindow(window);

        var root = new StackPanel { Margin = new Thickness(24, 22, 24, 18) };

        var heading = new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        if (kind == MessageDialogKind.Danger) heading.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
        else if (kind == MessageDialogKind.Warning) heading.Foreground = (Brush)Application.Current.FindResource("WarnBrush");
        root.Children.Add(heading);

        root.Children.Add(new TextBox
        {
            // 保存先のパスなどを写せるよう、選択できる文字にする（見た目は普通の文章）
            Text = message,
            IsReadOnly = true,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0),
        };

        Button Make(string text, MessageDialogResult value, string? styleKey)
        {
            var button = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            if (styleKey != null) button.Style = (Style)Application.Current.FindResource(styleKey);
            button.Click += (_, _) => { result = value; window.Close(); };
            return button;
        }

        // 取り消せない操作では、主ボタンを Enter で押せないようにする（うっかり Enter を押して消さないため）
        bool danger = kind == MessageDialogKind.Danger;
        var primary = Make(primaryText, MessageDialogResult.Primary, danger ? "DangerButton" : "PrimaryButton");
        primary.IsDefault = !danger;

        Button? secondary = secondaryText != null ? Make(secondaryText, MessageDialogResult.Secondary, null) : null;
        Button? cancel = cancelText != null ? Make(cancelText, MessageDialogResult.Cancel, null) : null;
        if (cancel != null)
        {
            cancel.IsCancel = true;
            if (danger) cancel.IsDefault = true;
        }
        else
        {
            primary.IsCancel = true;   // ボタンが 1 つなら、Esc でも閉じる
        }

        // 並びは Windows の慣例どおり、主な操作を左、キャンセルを右に置く
        buttons.Children.Add(primary);
        if (secondary != null) buttons.Children.Add(secondary);
        if (cancel != null) buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;
        window.Loaded += (_, _) => (danger && cancel != null ? cancel : primary).Focus();
        window.ShowDialog();
        return result;
    }
}
