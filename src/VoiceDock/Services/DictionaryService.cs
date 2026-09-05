using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// 辞書登録機能。社内用語・人名などの誤認識対策として、
/// 認識後の文字列を「誤認識語→正しい語」で強制置換する。
/// （Web Speech API では認識前のヒント指定ができないため、後段の置換で対応する）
/// データは %APPDATA%\VoiceDock\dictionary.json に保存し、CSV でエクスポート/インポートできる。
/// </summary>
public sealed class DictionaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly LogService _log;
    private readonly object _sync = new();
    private List<DictionaryEntry> _entries = new();

    /// <summary>読み込みに失敗したかどうか。失敗時は空の内容で上書きしない。</summary>
    private bool _loadFailed;

    /// <summary>読み込みに失敗して内容を復元できなかった場合 true。</summary>
    public bool LoadFailed => _loadFailed;

    private static bool IsValidJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch { return false; }
    }

    public DictionaryService(LogService log)
    {
        _log = log;
    }

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get { lock (_sync) return _entries.Select(e => new DictionaryEntry { Wrong = e.Wrong, Correct = e.Correct }).ToList(); }
    }

    public void Load()
    {
        try
        {
            // 破損時はバックアップから復帰する（辞書は再作成の手間が大きいため）
            var json = SafeFile.ReadAllText(AppPaths.DictionaryFile, IsValidJson);
            if (json != null)
            {
                lock (_sync)
                    _entries = JsonSerializer.Deserialize<List<DictionaryEntry>>(json, JsonOptions) ?? new();
            }
            else if (File.Exists(AppPaths.DictionaryFile))
            {
                _log.Error("辞書ファイルが読み取れませんでした。内容が失われないよう、空の状態では上書きしません");
                _loadFailed = true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"辞書ファイルの読み込みに失敗しました: {ex.Message}");
        }
    }

    public void Replace(IEnumerable<DictionaryEntry> entries)
    {
        lock (_sync)
        {
            _entries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Wrong) || !string.IsNullOrWhiteSpace(e.Correct))
                .Select(e => new DictionaryEntry { Wrong = e.Wrong.Trim(), Correct = e.Correct.Trim() })
                .ToList();
            Save();
        }
    }

    private void Save()
    {
        // 読み込みに失敗した状態で保存すると、壊れたファイルを空の内容で確定させてしまう
        if (_loadFailed)
        {
            _log.Warn("辞書の読み込みに失敗しているため、保存を見送りました");
            return;
        }
        try
        {
            AppPaths.EnsureDirectories();
            SafeFile.WriteAllText(AppPaths.DictionaryFile, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"辞書ファイルの保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>認識後テキストに「誤認識語→正しい語」の強制置換を適用する。</summary>
    public string ApplyReplacements(string text)
    {
        lock (_sync)
        {
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.Wrong) || string.IsNullOrEmpty(e.Correct)) continue;
                text = text.Replace(e.Wrong, e.Correct);
            }
        }
        return text;
    }

    /// <summary>CSV (UTF-8 BOM 付き、ヘッダー行あり) にエクスポートする。Excel での一括編集を想定。</summary>
    public void ExportCsv(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("誤認識語,正しい語");
        lock (_sync)
        {
            foreach (var e in _entries)
                sb.AppendLine($"{CsvEscape(e.Wrong)},{CsvEscape(e.Correct)}");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _log.Info($"辞書を CSV にエクスポートしました: {path}");
    }

    /// <summary>CSV からインポートして辞書全体を置き換える。戻り値は取り込んだ件数。</summary>
    public int ImportCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var rows = ParseCsv(text);
        var entries = new List<DictionaryEntry>();
        foreach (var row in rows)
        {
            if (row.Count == 0) continue;
            var wrong = row.ElementAtOrDefault(0)?.Trim() ?? "";
            var correct = row.ElementAtOrDefault(1)?.Trim() ?? "";
            if (wrong == "誤認識語" && correct == "正しい語") continue; // ヘッダー行
            if (wrong.Length == 0 && correct.Length == 0) continue;
            entries.Add(new DictionaryEntry { Wrong = wrong, Correct = correct });
        }
        Replace(entries);
        _log.Info($"辞書を CSV からインポートしました: {path} ({entries.Count} 件)");
        return entries.Count;
    }

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return $"\"{s.Replace("\"", "\"\"")}\"";
        return s;
    }

    private static List<List<string>> ParseCsv(string text)
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
