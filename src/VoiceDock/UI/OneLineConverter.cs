using System.Globalization;
using System.Windows.Data;

namespace VoiceDock.UI;

/// <summary>
/// 複数行の文字列を、一覧で 1 行に収まるよう改行を「⏎」に置き換えて表示する。
/// 定型文の本文（住所・署名など）の一覧表示に使う。
/// </summary>
public sealed class OneLineConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ ") : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
