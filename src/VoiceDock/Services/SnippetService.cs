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
        lock (_sync)
        {
            foreach (var e in _entries)
            {
                if (string.Equals(e.Phrase, phrase, StringComparison.OrdinalIgnoreCase))
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
}
