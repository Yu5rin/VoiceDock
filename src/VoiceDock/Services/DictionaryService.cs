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
        get { lock (_sync) return _entries.Select(e => e.Clone()).ToList(); }
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
            // 辞書画面は開いた時点の複製を持っているため、その後の音声入力で増えた使用回数を
            // 画面側の古い回数で上書きしないよう、同じ読みの項目は多いほうの回数を残す
            var current = new Dictionary<string, DictionaryEntry>(StringComparer.Ordinal);
            foreach (var e in _entries)
                current.TryAdd(KanaNormalizer.NormalizeKey(e.Wrong), e);

            _entries = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Wrong) || !string.IsNullOrWhiteSpace(e.Correct))
                .Select(e =>
                {
                    var entry = e.Clone();
                    entry.Wrong = e.Wrong.Trim();
                    entry.Correct = e.Correct.Trim();
                    if (current.TryGetValue(KanaNormalizer.NormalizeKey(entry.Wrong), out var old) &&
                        old.UseCount > entry.UseCount)
                    {
                        entry.UseCount = old.UseCount;
                        entry.LastUsed = old.LastUsed;
                    }
                    return entry;
                })
                .ToList();
            _usageDirty = false;
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
    ///
    /// 「発話全体が一致したときだけ」の項目は、文の一部には当てない。
    /// </summary>
    /// <param name="countUsage">
    /// 置き換えに使った項目の使用回数を数えるか。実際の音声入力では true、
    /// 辞書画面の「試す」欄では false にする（試しただけで回数が増えないように）。
    /// </param>
    public string ApplyReplacements(string text, bool countUsage = false)
    {
        List<DictionaryEntry> entries;
        lock (_sync) entries = _entries.ToList();
        var usable = entries.Where(e => !string.IsNullOrEmpty(e.Wrong) && !string.IsNullOrEmpty(e.Correct)).ToList();

        // 発話全体が一致する項目があれば、それだけで置き換えて終わる。
        // 認識結果の末尾に付いた句読点は残す
        var body = text.TrimEnd('。', '、', '.', ',', '！', '!', '？', '?');
        var key = KanaNormalizer.NormalizeKey(body);
        var whole = usable.FirstOrDefault(e => e.WholeOnly && KanaNormalizer.NormalizeKey(e.Wrong) == key);
        if (whole != null && key.Length > 0)
        {
            if (countUsage) CountUsage(whole);
            // 置き換え後の表記が句読点で終わっていれば、句点が重ならないよう末尾は足さない
            var tail = text[body.Length..];
            bool endsWithPunctuation = whole.Correct.Length > 0 && "。、.,！!？?".Contains(whole.Correct[^1]);
            return endsWithPunctuation ? whole.Correct : whole.Correct + tail;
        }

        // 長い語から先に当てる。短い語を先に置き換えると、
        // それを含む長い語が壊れて当たらなくなるため。
        foreach (var e in usable.Where(e => !e.WholeOnly).OrderByDescending(e => e.Wrong.Length))
        {
            var replaced = KanaNormalizer.ReplaceLoosely(text, e.Wrong, e.Correct);
            if (replaced == text) continue;
            text = replaced;
            if (countUsage) CountUsage(e);
        }
        return text;
    }

    /// <summary>使用回数の保存を待っている変更があるか。</summary>
    private bool _usageDirty;

    /// <summary>使用回数の保存をまとめて行うためのタイマー。</summary>
    private System.Threading.Timer? _usageSaveTimer;

    /// <summary>使用回数の保存は、音声入力のたびに書き込まないよう、少しまとめてから行う。</summary>
    private static readonly TimeSpan UsageSaveDelay = TimeSpan.FromSeconds(10);

    private void CountUsage(DictionaryEntry entry)
    {
        lock (_sync)
        {
            // 画面の保存などで項目が入れ替わっていたら数えない（次の置き換えから数える）
            if (!_entries.Contains(entry)) return;
            entry.UseCount++;
            entry.LastUsed = DateTime.Now;
            _usageDirty = true;
            _usageSaveTimer ??= new System.Threading.Timer(_ => Flush());
            _usageSaveTimer.Change(UsageSaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>まだ保存していない使用回数を保存する（アプリ終了時にも呼ぶ）。</summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (!_usageDirty) return;
            _usageDirty = false;
            Save();
        }
    }

    /// <summary>CSV の見出し。以前の版で書き出した見出しも読み込み時に受け付ける。</summary>
    private const string CsvHeaderWrong = "読み・誤認識語";
    private const string CsvHeaderCorrect = "出したい表記";

    private const string CsvHeaderMatch = "一致のしかた";

    /// <summary>「発話全体が一致したときだけ」を表す、CSV や貼り付けでの書き方。</summary>
    private const string WholeOnlyLabel = "全体のみ";

    /// <summary>CSV・Excel 貼り付けの 3 列目を「発話全体が一致したときだけ」と読むか。</summary>
    public static bool ParseWholeOnly(string? cell)
    {
        var c = (cell ?? "").Trim();
        return c is WholeOnlyLabel or "全体" or "完全一致" or "はい" or "○" or "1"
               || c.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>CSV (UTF-8 BOM 付き、見出し行あり) にエクスポートする。Excel での一括編集を想定。</summary>
    public void ExportCsv(string path)
    {
        List<DictionaryEntry> entries;
        lock (_sync) entries = _entries.ToList();
        Csv.Write(path, new[] { CsvHeaderWrong, CsvHeaderCorrect, CsvHeaderMatch },
            entries.Select(e => (IReadOnlyList<string>)new[] { e.Wrong, e.Correct, e.WholeOnly ? WholeOnlyLabel : "" }));
        _log.Info($"辞書を CSV にエクスポートしました: {path}");
    }

    /// <summary>
    /// CSV からインポートして辞書全体を置き換える。戻り値は取り込んだ件数。
    /// 3 列目（一致のしかた）が無い以前の形式の CSV も読める。
    /// </summary>
    public int ImportCsv(string path)
    {
        var rows = Csv.Read(path, 3, h =>
            (h[0] == CsvHeaderWrong && h[1] == CsvHeaderCorrect) || (h[0] == "誤認識語" && h[1] == "正しい語"));
        var entries = rows
            .Select(r => new DictionaryEntry { Wrong = r[0].Trim(), Correct = r[1].Trim(), WholeOnly = ParseWholeOnly(r[2]) })
            .ToList();
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
