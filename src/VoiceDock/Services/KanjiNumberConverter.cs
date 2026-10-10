using System.Text;
using System.Text.RegularExpressions;

namespace VoiceDock.Services;

/// <summary>
/// 漢数字を算用数字にそろえる（例:「三時半」→「3時半」、「二〇二六年」→「2026年」、「三億五千万円」→「3億5000万円」）。
///
/// 認識エンジンは数を漢数字で返すことがあるが、仕事の文章では算用数字で書くことが多い。
/// ただし「一緒」「一般」「一人」「十分（じゅうぶん）」のように、漢数字のまま書く言葉も多いため、
/// 次の両方を満たすときだけ変換する。
/// ・漢数字の直後に、数を数える言葉（時・分・年・月・日・円・個・回・人 など）が続く
/// ・言葉として決まった書き方（例外の一覧）に当たらない
/// 「一」1 文字だけの場合は、言葉の一部であることが特に多いため、はっきり数を表す言葉が続くときだけ変換する。
/// 万・億・兆は単位として残し、それより下の位を数字にする（新聞などと同じ書き方）。
/// </summary>
public static class KanjiNumberConverter
{
    private static readonly Regex Run = new("[〇零一二三四五六七八九十百千万億兆]+", RegexOptions.Compiled);

    /// <summary>数を数える言葉。長いものから照合する。</summary>
    private static readonly string[] Counters = new[]
    {
        "パーセント", "メートル", "キロ", "センチ", "ミリ", "グラム", "ドル", "ユーロ", "ページ",
        "時間", "週間", "か月", "ヶ月", "カ月", "ケ月", "箇月", "年間", "日間", "年度", "年生", "世紀", "時半",
        "時", "分", "秒", "年", "月", "日", "週", "円", "個", "回", "件", "本", "枚", "台", "歳", "才", "階",
        "倍", "割", "人", "名", "社", "冊", "匹", "頭", "杯", "号", "位", "番", "章", "条", "項", "行", "列",
        "歩", "点", "度", "部", "%", "％",
    }.OrderByDescending(c => c.Length).ToArray();

    /// <summary>「一」1 文字のときに変換する、はっきり数を表す言葉。「一人」「一番」「一度」「一部」などは漢数字のまま残す。</summary>
    private static readonly HashSet<string> CountersForSingleOne = new()
    {
        "パーセント", "メートル", "キロ", "センチ", "ミリ", "グラム", "ドル", "ユーロ", "ページ",
        "時間", "週間", "か月", "ヶ月", "カ月", "ケ月", "箇月", "年間", "日間", "年度", "年生", "世紀", "時半",
        "時", "秒", "年", "月", "円", "個", "回", "件", "枚", "台", "歳", "才", "階", "倍", "割", "名", "社", "冊",
        "%", "％",
    };

    /// <summary>漢数字のまま書く言葉。この範囲にかかる漢数字は変換しない。</summary>
    private static readonly string[] KeepWords =
    {
        "三日月", "三日坊主", "十人十色", "一人一人", "百人一首", "八百屋", "八百長", "万一", "千差万別",
        "一石二鳥", "一長一短", "一進一退", "一期一会", "一日中", "十二分", "四六時中", "二人三脚",
        "九分九厘", "五分五分", "七五三", "百年の恋", "五月雨", "五月晴れ",
        "一時的", "一時停止", "一時保存", "一時期", "一時金", "一時中断", "一時帰", "一時預",
        "一人称", "二人称", "三人称", "億万長者", "十八番", "二番煎じ",
    };

    private const string BigUnits = "万億兆";

    /// <summary>文中の漢数字を、決まりに当てはまるものだけ算用数字にする。</summary>
    public static string Convert(string text)
    {
        if (text.Length == 0) return text;
        var keepRanges = FindKeepRanges(text);
        return Run.Replace(text, m => ConvertRun(text, m, keepRanges));
    }

    private static List<(int Start, int End)> FindKeepRanges(string text)
    {
        var ranges = new List<(int, int)>();
        foreach (var word in KeepWords)
        {
            for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(word, i + 1, StringComparison.Ordinal))
                ranges.Add((i, i + word.Length));
        }
        return ranges;
    }

    private static string ConvertRun(string text, Match m, List<(int Start, int End)> keepRanges)
    {
        var run = m.Value;
        int start = m.Index, end = m.Index + m.Length;

        if (keepRanges.Any(r => start < r.End && r.Start < end)) return run;

        // 「万」「億」だけ（「万年筆」「万歳」「万人受け」「億劫」）は言葉の一部。数として読むのは「一万」「十億」のように前に数が付くとき
        if (run.Length == 1 && BigUnits.Contains(run[0])) return run;

        // 「3千円」のように数字と混ざっている場合は、書いたとおりに残す
        if (start > 0 && IsDigit(text[start - 1])) return run;
        if (end < text.Length && IsDigit(text[end])) return run;

        var rest = text.AsSpan(end);
        string? counter = null;
        foreach (var c in Counters)
        {
            if (rest.StartsWith(c, StringComparison.Ordinal)) { counter = c; break; }
        }
        // 万・億・兆で終わる数（「一万」「三億」）は、続く言葉が無くても数とわかる
        if (counter == null && !(run.Length > 1 && BigUnits.Contains(run[^1]))) return run;

        if (!TryParse(run, out var value, out bool positional)) return run;

        char prev = start > 0 ? text[start - 1] : '\0';
        if (counter != null)
        {
            // 「三分の一」のような分数は、漢数字のまま書くことが多い
            if (counter == "分" && rest.StartsWith("分の", StringComparison.Ordinal)) return run;
            // 「十分（じゅうぶん）」は、「三時十分」のように時刻の続きのときだけ数として扱う
            if (run == "十" && counter == "分" && prev != '時') return run;

            if (run == "一")
            {
                bool strong = CountersForSingleOne.Contains(counter)
                              // 「四月一日」の「一日」、「三時一分」の「一分」は数
                              || (counter == "日" && prev == '月')
                              || (counter == "分" && prev == '時');
                if (!strong) return run;
            }
        }

        return Format(run, value, positional);
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9' or >= '０' and <= '９';

    private static int DigitOf(char c) => c switch
    {
        '〇' or '零' => 0, '一' => 1, '二' => 2, '三' => 3, '四' => 4,
        '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1,
    };

    private static long UnitOf(char c) => c switch
    {
        '十' => 10, '百' => 100, '千' => 1000,
        '万' => 10_000, '億' => 100_000_000, '兆' => 1_000_000_000_000, _ => 0,
    };

    /// <summary>
    /// 漢数字を数にする。「二〇二六」のように位の字が無いものは 1 字ずつの数字として読む（positional = false）。
    /// 「二二十」のような読めない並びは false。
    /// </summary>
    internal static bool TryParse(string run, out long value, out bool positional)
    {
        value = 0;
        positional = run.Any(c => UnitOf(c) > 0);
        if (!positional)
        {
            foreach (var c in run) value = value * 10 + DigitOf(c);
            return run.Length <= 18;
        }

        long total = 0, section = 0, current = -1;
        long lastSmall = long.MaxValue, lastBig = long.MaxValue;
        foreach (var c in run)
        {
            int d = DigitOf(c);
            if (d >= 0)
            {
                if (current >= 0) return false;   // 数字が 2 つ続く（「二二十」）
                current = d;
                continue;
            }
            long unit = UnitOf(c);
            if (unit < 10_000)
            {
                if (unit >= lastSmall) return false;   // 「十百」のように位が上がっていく
                section += (current < 0 ? 1 : current) * unit;
                lastSmall = unit;
            }
            else
            {
                if (unit >= lastBig) return false;
                long part = section + Math.Max(current, 0);
                if (part == 0) part = 1;   // 「万円」の「万」だけ、は 1 万とみなす
                total += part * unit;
                section = 0;
                lastBig = unit;
                lastSmall = long.MaxValue;
            }
            current = -1;
        }
        value = total + section + Math.Max(current, 0);
        return true;
    }

    /// <summary>算用数字の表記にする。万・億・兆を含む数は、その単位を残す（「3億5000万」）。</summary>
    private static string Format(string run, long value, bool positional)
    {
        if (!positional)
        {
            // 「二〇二六」→「2026」。先頭の 0 も読まれたとおりに残す（「〇三」→「03」）
            var sb = new StringBuilder(run.Length);
            foreach (var c in run) sb.Append((char)('0' + DigitOf(c)));
            return sb.ToString();
        }

        if (!run.Any(c => BigUnits.Contains(c))) return value.ToString();

        var result = new StringBuilder();
        foreach (var (unit, name) in new[] { (1_000_000_000_000L, "兆"), (100_000_000L, "億"), (10_000L, "万") })
        {
            long q = value / unit % 10_000;
            if (unit == 1_000_000_000_000L) q = value / unit;
            if (q > 0) result.Append(q).Append(name);
        }
        long rest = value % 10_000;
        if (rest > 0) result.Append(rest);
        return result.Length > 0 ? result.ToString() : "0";
    }
}
