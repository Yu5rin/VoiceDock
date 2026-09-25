using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// 定型文スニペット。「読み」だけを発話したときに、登録した長文へ展開する。
/// 例: 「じゅうしょ」→ 会社の住所全文、「めーる」→ メールアドレス。
/// 誤爆を避けるため、発話全体が読みと一致した場合のみ展開する
/// （文中に含まれるだけでは展開しない）。
/// データは %APPDATA%\VoiceDock\snippets.json に保存する。
/// </summary>
public sealed class SnippetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly LogService _log;
    private readonly object _sync = new();
    private List<SnippetEntry> _entries = new();
    private bool _loadFailed;

    /// <summary>読み込みに失敗して内容を復元できなかった場合 true。</summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>本体が壊れていて、控え (.bak) から復帰した場合 true。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>保存に失敗したときに発火（引数は理由の説明）。</summary>
    public event Action<string>? SaveFailed;

    /// <summary>内容が定型文として読み込めるか（破損判定に使う）。</summary>
    private static bool CanDeserialize(string text)
    {
        try { return JsonSerializer.Deserialize<List<SnippetEntry>>(text, JsonOptions) != null; }
        catch { return false; }
    }

    public SnippetService(LogService log)
    {
        _log = log;
    }

    public IReadOnlyList<SnippetEntry> Entries
    {
        get
        {
            lock (_sync)
                return _entries.Select(e => new SnippetEntry { Phrase = e.Phrase, Expansion = e.Expansion }).ToList();
        }
    }

    public void Load()
    {
        try
        {
            // 破損時はバックアップから復帰する
            var json = SafeFile.ReadAllText(AppPaths.SnippetsFile, CanDeserialize, out bool fromBackup);
            if (json != null)
            {
                RecoveredFromBackup = fromBackup;
                if (fromBackup)
                    _log.Warn("定型文ファイルが壊れていたため、バックアップから復帰しました");
                lock (_sync)
                    _entries = JsonSerializer.Deserialize<List<SnippetEntry>>(json, JsonOptions) ?? new();
            }
            else if (File.Exists(AppPaths.SnippetsFile))
            {
                _log.Error("定型文ファイルが読み取れませんでした。内容が失われないよう、空の状態では上書きしません");
                _loadFailed = true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"定型文ファイルの読み込みに失敗しました: {ex.Message}");
            _loadFailed = true;
        }
    }

    public void Replace(IEnumerable<SnippetEntry> entries)
    {
        lock (_sync)
        {
            _entries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Phrase) && !string.IsNullOrWhiteSpace(e.Expansion))
                .Select(e => new SnippetEntry { Phrase = e.Phrase.Trim(), Expansion = e.Expansion })
                .ToList();
            Save();
        }
    }

    /// <summary>
    /// 発話全体がいずれかの読みと一致すれば、展開後テキストを返す。
    /// 一致しなければ null。
    /// </summary>
    public string? TryExpand(string phrase)
    {
        // 「じゅうしょ」と登録していても「ジュウショ」と認識されることがあるため、
        // 辞書と同じく、かなや英字の表記ゆれを無視して比べる
        var key = KanaNormalizer.NormalizeKey(phrase);
        if (key.Length == 0) return null;
        lock (_sync)
        {
            foreach (var e in _entries)
            {
                if (KanaNormalizer.NormalizeKey(e.Phrase) == key)
                    return e.Expansion;
            }
        }
        return null;
    }

    private void Save()
    {
        if (_loadFailed)
        {
            _log.Warn("定型文の読み込みに失敗しているため、保存を見送りました");
            return;
        }
        try
        {
            AppPaths.EnsureDirectories();
            SafeFile.WriteAllText(AppPaths.SnippetsFile, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"定型文ファイルの保存に失敗しました: {ex.Message}");
            SaveFailed?.Invoke(UserMessage.Describe(ex));
        }
    }

    private const string CsvHeaderPhrase = "読み（発話）";
    private const string CsvHeaderExpansion = "入力する本文";

    /// <summary>CSV (UTF-8 BOM 付き、見出し行あり) にエクスポートする。本文の改行もそのまま残る。</summary>
    public void ExportCsv(string path)
    {
        List<SnippetEntry> entries;
        lock (_sync) entries = _entries.ToList();
        Csv.Write(path, CsvHeaderPhrase, CsvHeaderExpansion, entries.Select(e => (e.Phrase, e.Expansion)));
        _log.Info($"定型文を CSV にエクスポートしました: {path}");
    }

    /// <summary>CSV からインポートして定型文全体を置き換える。戻り値は取り込んだ件数。</summary>
    public int ImportCsv(string path)
    {
        var rows = Csv.Read(path, (a, b) => a == CsvHeaderPhrase && b == CsvHeaderExpansion);
        var entries = rows
            .Select(r => new SnippetEntry
            {
                Phrase = r.A.Trim(),
                // Excel は改行を CRLF で書くことがあるため、LF にそろえる
                Expansion = r.B.Replace("\r\n", "\n").Replace('\r', '\n'),
            })
            .ToList();
        Replace(entries);
        _log.Info($"定型文を CSV からインポートしました: {path} ({entries.Count} 件)");
        return entries.Count;
    }

    /// <summary>
    /// バックアップから復元する。読み込みに失敗していた場合も、
    /// 利用者が明示的に置き換えを選んだので保存を許可する。
    /// </summary>
    public void RestoreFrom(IEnumerable<SnippetEntry> entries)
    {
        _loadFailed = false;
        Replace(entries);
    }
}
