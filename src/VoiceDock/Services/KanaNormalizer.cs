using System.Text;

namespace VoiceDock.Services;

/// <summary>
/// 辞書の照合に使う、かなの表記ゆれを吸収する正規化。
///
/// Web Speech API は変換済みの文章だけを返し、読み（かな）は返さない。
/// そのため IME のような「読み → 漢字」の辞書は本来引けないが、
/// 認識エンジンは知らない語をかなのまま出すことが多く、そこが辞書の出番になる。
/// ひらがな・カタカナ・半角カナを同じものとして扱うことで、
/// 「やまだ」と登録しておけば「ヤマダ」でも「ﾔﾏﾀﾞ」でも当たるようにする。
///
/// 置き換えは元の文字列に対して行うため、正規化した文字が元のどこから来たかを
/// 一緒に記録し、見つけた位置を元の文字列の範囲へ戻せるようにしている。
/// </summary>
internal static class KanaNormalizer
{
    /// <summary>半角カナ (U+FF66..U+FF9F) を全角カタカナへ対応づける表。</summary>
    private const string HalfWidthKana =
        "ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜";

    private const char HalfWidthKanaFirst = 'ｦ';
    private const char HalfWidthKanaLast = 'ﾟ';

    /// <summary>濁点を付けられる文字と、付けた後の文字の組。</summary>
    private const string VoicedPairs =
        "カガキギクグケゲコゴサザシジスズセゼソゾタダチヂツヅテデトドハバヒビフブヘベホボウヴ";

    /// <summary>半濁点を付けられる文字と、付けた後の文字の組。</summary>
    private const string SemiVoicedPairs = "ハパヒピフプヘペホポ";

    /// <summary>正規化後の 1 文字が、元の文字列のどこから来たか。</summary>
    private readonly record struct Origin(int Start, int Length);

    /// <summary>
    /// 表記ゆれを吸収した文字列に変換する。
    /// かなはひらがなに、英数字は半角小文字にそろえる。
    /// </summary>
    private static string Normalize(string text, out List<Origin> origins)
    {
        var sb = new StringBuilder(text.Length);
        origins = new List<Origin>(text.Length);

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int consumed = 1;

            // 半角カナ → 全角カタカナ
            if (c >= HalfWidthKanaFirst && c <= HalfWidthKanaLast)
                c = HalfWidthKana[c - HalfWidthKanaFirst];

            // 続く濁点・半濁点を 1 文字にまとめる（半角・全角・結合文字のいずれも受ける）
            if (i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next is 'ﾞ' or '゛' or '゙')
                {
                    char v = Combine(VoicedPairs, c);
                    if (v != '\0') { c = v; consumed = 2; }
                }
                else if (next is 'ﾟ' or '゜' or '゚')
                {
                    char v = Combine(SemiVoicedPairs, c);
                    if (v != '\0') { c = v; consumed = 2; }
                }
            }

            // カタカナ → ひらがな（長音記号「ー」は両方で共通なのでそのまま）
            if (c is >= 'ァ' and <= 'ヶ')
                c = (char)(c - 0x60);

            // 全角英数記号 → 半角
            if (c is >= '！' and <= '～')
                c = (char)(c - 0xFEE0);

            c = char.ToLowerInvariant(c);

            sb.Append(c);
            origins.Add(new Origin(i, consumed));
            i += consumed;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 表記ゆれを吸収した比較用の文字列を返す。
    /// 「やまだ」と「ヤマダ」を同じ語として扱う重複チェックや検索に使う。
    /// </summary>
    public static string NormalizeKey(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Normalize(text.Trim(), out _);

    /// <summary>濁点・半濁点を付けた文字を返す。付けられない場合は '\0'。</summary>
    private static char Combine(string pairs, char baseChar)
    {
        int i = pairs.IndexOf(baseChar);
        // 組の先頭（偶数位置）に見つかったときだけ、濁点付きの文字がある
        return i >= 0 && i % 2 == 0 ? pairs[i + 1] : '\0';
    }

    /// <summary>
    /// 表記ゆれを無視して <paramref name="needle"/> を探し、
    /// 見つかった箇所を <paramref name="replacement"/> に置き換える。
    /// 置き換えた部分は探し直さないため、入れ替えが連鎖することはない。
    /// </summary>
    public static string ReplaceLoosely(string text, string needle, string replacement)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return text;

        var normalized = Normalize(text, out var origins);
        var normalizedNeedle = Normalize(needle, out _);
        if (normalizedNeedle.Length == 0) return text;

        var sb = new StringBuilder(text.Length);
        int copied = 0;   // 元の文字列のうち、どこまで書き出したか
        int pos = 0;

        while (pos <= normalized.Length - normalizedNeedle.Length)
        {
            if (string.CompareOrdinal(normalized, pos, normalizedNeedle, 0, normalizedNeedle.Length) != 0)
            {
                pos++;
                continue;
            }

            var first = origins[pos];
            var last = origins[pos + normalizedNeedle.Length - 1];
            int start = first.Start;
            int end = last.Start + last.Length;

            sb.Append(text, copied, start - copied);
            sb.Append(replacement);
            copied = end;
            pos += normalizedNeedle.Length;
        }

        if (copied == 0) return text;   // 1 つも見つからなかった

        sb.Append(text, copied, text.Length - copied);
        return sb.ToString();
    }
}
