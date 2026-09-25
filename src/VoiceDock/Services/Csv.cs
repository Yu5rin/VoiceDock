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
    public static void Write(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, headers);
        foreach (var row in rows) AppendRow(sb, row);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// CSV ファイルを読み、各行を <paramref name="columns"/> 列にそろえて返す（足りない列は空文字）。
    /// <paramref name="isHeader"/> が true を返す行（見出し）と、すべて空の行は読み飛ばす。
    /// </summary>
    public static List<string[]> Read(string path, int columns, Func<string[], bool> isHeader)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var result = new List<string[]>();
        foreach (var row in Parse(text))
        {
            var cells = Enumerable.Range(0, columns).Select(i => row.ElementAtOrDefault(i) ?? "").ToArray();
            if (cells.All(c => c.Trim().Length == 0)) continue;
            if (isHeader(cells.Select(c => c.Trim()).ToArray())) continue;
            result.Add(cells);
        }
        return result;
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(cells[i]));
        }
        sb.Append("\r\n");
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
