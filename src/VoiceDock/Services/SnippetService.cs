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

    private static bool IsValidJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
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
            var json = SafeFile.ReadAllText(AppPaths.SnippetsFile, IsValidJson);
            if (json != null)
            {
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
        }
    }
}
