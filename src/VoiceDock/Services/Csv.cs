using System.IO;
using System.Text;

namespace VoiceDock.Services;

/// <summary>
/// 辞書・定型文の CSV 入出力に使う最小限の読み書き。
/// Excel で開いて編集できるよう、UTF-8（BOM 付き）で書き出し、
/// 引用符で囲んだセル内の改行・カンマ・引用符に対応する。
/// </summary>
internal static class Csv
{
    /// <summary>1 行目を見出しとして、行の一覧を CSV ファイルに書き出す。</summary>
    public static void Write(string path, string header1, string header2,
        IEnumerable<(string A, string B)> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Escape(header1)).Append(',').Append(Escape(header2)).Append("\r\n");
        foreach (var (a, b) in rows)
            sb.Append(Escape(a)).Append(',').Append(Escape(b)).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// CSV ファイルを読み、2 列ずつの組にして返す。
    /// <paramref name="isHeader"/> が true を返す行（見出し）は読み飛ばす。
    /// </summary>
    public static List<(string A, string B)> Read(string path, Func<string, string, bool> isHeader)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var result = new List<(string, string)>();
        foreach (var row in Parse(text))
        {
            if (row.Count == 0) continue;
            var a = row.ElementAtOrDefault(0) ?? "";
            var b = row.ElementAtOrDefault(1) ?? "";
            if (isHeader(a.Trim(), b.Trim())) continue;
            if (a.Trim().Length == 0 && b.Trim().Length == 0) continue;
            result.Add((a, b));
        }
        return result;
    }

    private static string Escape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ',': row.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        rows.Add(row); row = new List<string>();
                        break;
                    default: field.Append(c); break;
                }
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
