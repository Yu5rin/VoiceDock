using System.Text;
using System.Windows.Input;

namespace VoiceDock.Models;

/// <summary>
/// ホットキー（修飾キー＋メインキー）の表現。文字列 "Ctrl+Alt+Space" 形式と相互変換する。
/// </summary>
public readonly record struct HotkeySpec(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var partRaw in text.Split('+'))
        {
            var part = partRaw.Trim();
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control; break;
                case "alt":
                    modifiers |= ModifierKeys.Alt; break;
                case "shift":
                    modifiers |= ModifierKeys.Shift; break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!TryParseKey(part, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        spec = new HotkeySpec(modifiers, key);
        return true;
    }

    /// <summary>
    /// キー名を解釈する。
    ///
    /// Enum.TryParse は "1" のような数字を「列挙体の値 1」として受け付けてしまい、
    /// Ctrl+1 が Ctrl+Cancel になってしまう。数字は D1 などのキー名へ読み替え、
    /// 数値そのものは受け付けない。
    /// </summary>
    private static bool TryParseKey(string part, out Key key)
    {
        key = Key.None;
        if (part.Length == 0) return false;

        // "1" → "D1"、"Num1" のような表記も拾う
        if (part.Length == 1 && part[0] is >= '0' and <= '9')
            part = "D" + part;
        else if (int.TryParse(part, out _))
            return false;

        return Enum.TryParse(part, ignoreCase: true, out key)
               && Enum.IsDefined(typeof(Key), key)
               && key != Key.None;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Key);
        return sb.ToString();
    }
}
