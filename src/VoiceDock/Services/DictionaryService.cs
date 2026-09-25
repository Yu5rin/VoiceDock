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

    /// <summary>本体が壊れていて、控え (.bak) から復帰した場合 true。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>保存に失敗したときに発火（引数は理由の説明）。</summary>
    public event Action<string>? SaveFailed;

    /// <summary>内容が辞書として読み込めるか（破損判定に使う）。</summary>
    private static bool CanDeserialize(string text)
    {
        try { return JsonSerializer.Deserialize<List<DictionaryEntry>>(text, JsonOptions) != null; }
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
            var json = SafeFile.ReadAllText(AppPaths.DictionaryFile, CanDeserialize, out bool fromBackup);
            if (json != null)
            {
                RecoveredFromBackup = fromBackup;
                if (fromBackup)
                    _log.Warn("辞書ファイルが壊れていたため、バックアップから復帰しました");
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
            // 読めなかった内容を空で上書きしないよう、失敗として記録する
            _log.Error($"辞書ファイルの読み込みに失敗しました: {ex.Message}");
            _loadFailed = true;
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
            SaveFailed?.Invoke(UserMessage.Describe(ex));
        }
    }

    /// <summary>
    /// 認識後テキストに辞書の強制置換を適用する。
    ///
    /// 照合はひらがな・カタカナ・半角カナ・英字の大文字小文字を区別しない。
    /// 認識エンジンは知らない語をかなのまま出すことが多いため、
    /// 読みで登録しておけば（例:「やまだ」→「山田」）、出力が
    /// 「やまだ」でも「ヤマダ」でも当たるようになる。
    /// </summary>
    public string ApplyReplacements(string text)
    {
        List<DictionaryEntry> entries;
        lock (_sync) entries = _entries.ToList();

        // 長い語から先に当てる。短い語を先に置き換えると、
        // それを含む長い語が壊れて当たらなくなるため。
        foreach (var e in entries
                     .Where(e => !string.IsNullOrEmpty(e.Wrong) && !string.IsNullOrEmpty(e.Correct))
                     .OrderByDescending(e => e.Wrong.Length))
        {
            text = KanaNormalizer.ReplaceLoosely(text, e.Wrong, e.Correct);
        }
        return text;
    }

    /// <summary>CSV の見出し。以前の版で書き出した見出しも読み込み時に受け付ける。</summary>
    private const string CsvHeaderWrong = "読み・誤認識語";
    private const string CsvHeaderCorrect = "出したい表記";

    /// <summary>CSV (UTF-8 BOM 付き、見出し行あり) にエクスポートする。Excel での一括編集を想定。</summary>
    public void ExportCsv(string path)
    {
        List<DictionaryEntry> entries;
        lock (_sync) entries = _entries.ToList();
        Csv.Write(path, CsvHeaderWrong, CsvHeaderCorrect, entries.Select(e => (e.Wrong, e.Correct)));
        _log.Info($"辞書を CSV にエクスポートしました: {path}");
    }

    /// <summary>CSV からインポートして辞書全体を置き換える。戻り値は取り込んだ件数。</summary>
    public int ImportCsv(string path)
    {
        var rows = Csv.Read(path, (a, b) =>
            (a == CsvHeaderWrong && b == CsvHeaderCorrect) || (a == "誤認識語" && b == "正しい語"));
        var entries = rows.Select(r => new DictionaryEntry { Wrong = r.A.Trim(), Correct = r.B.Trim() }).ToList();
        Replace(entries);
        _log.Info($"辞書を CSV からインポートしました: {path} ({entries.Count} 件)");
        return entries.Count;
    }

    /// <summary>
    /// バックアップから復元する。読み込みに失敗していた場合も、
    /// 利用者が明示的に置き換えを選んだので保存を許可する。
    /// </summary>
    public void RestoreFrom(IEnumerable<DictionaryEntry> entries)
    {
        _loadFailed = false;
        Replace(entries);
    }
}
