using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>バックアップファイルから読み取った内容。</summary>
public sealed record BackupContents(
    AppSettings Settings,
    List<DictionaryEntry> Dictionary,
    List<SnippetEntry> Snippets,
    string? AppVersion,
    DateTime? CreatedAt);

/// <summary>
/// 設定・辞書・定型文をまとめて 1 つのファイルに保存・復元する。
/// PC の入れ替えや再インストールのときに、登録した内容をまとめて移せるようにするため。
///
/// 中身は人が読める JSON にしている。設定は復元時に設定ファイルと同じ補正を通すため、
/// 手で書き換えたファイルを読み込ませても、危険な値（更新の確認先など）は適用されない。
/// </summary>
public sealed class BackupService
{
    /// <summary>VoiceDock のバックアップであることを示す印。</summary>
    private const string FormatName = "VoiceDock-backup";

    /// <summary>ファイル形式の版。中身の構造を変えたら上げる。</summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly SettingsService _settings;
    private readonly DictionaryService _dictionary;
    private readonly SnippetService _snippets;
    private readonly LogService _log;

    public BackupService(SettingsService settings, DictionaryService dictionary,
        SnippetService snippets, LogService log)
    {
        _settings = settings;
        _dictionary = dictionary;
        _snippets = snippets;
        _log = log;
    }

    /// <summary>現在の設定・辞書・定型文を 1 つのファイルに保存する。</summary>
    public void Save(string path)
    {
        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["version"] = FormatVersion,
            ["appVersion"] = UpdateService.CurrentVersion.ToString(),
            ["createdAt"] = DateTime.Now.ToString("o"),
            ["settings"] = JsonNode.Parse(_settings.ExportJson()),
            ["dictionary"] = JsonSerializer.SerializeToNode(_dictionary.Entries),
            ["snippets"] = JsonSerializer.SerializeToNode(_snippets.Entries),
        };
        // 利用者が選んだ場所に書くため、.bak などの副産物は作らない
        File.WriteAllText(path, root.ToJsonString(WriteOptions));
        _log.Info($"バックアップを保存しました: {path}");
    }

    /// <summary>
    /// バックアップファイルを読み取る（まだ適用はしない）。
    /// VoiceDock のバックアップでない・壊れている場合は、理由を添えて例外を投げる。
    /// </summary>
    public static BackupContents Load(string path)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw new InvalidDataException("ファイルの内容を読み取れませんでした。壊れているか、別の種類のファイルです。");
        }

        if (root is not JsonObject obj || ReadString(obj, "format") != FormatName)
            throw new InvalidDataException("VoiceDock のバックアップファイルではありません。");

        int version = ReadInt(obj, "version");
        if (version > FormatVersion)
            throw new InvalidDataException("新しいバージョンの VoiceDock で作られたバックアップのため、読み込めません。VoiceDock を更新してからお試しください。");

        var settings = SettingsService.TryParse(obj["settings"]?.ToJsonString() ?? "")
                       ?? throw new InvalidDataException("バックアップの設定部分を読み取れませんでした。");

        List<DictionaryEntry> dictionary;
        List<SnippetEntry> snippets;
        try
        {
            dictionary = obj["dictionary"]?.Deserialize<List<DictionaryEntry>>() ?? new();
            snippets = obj["snippets"]?.Deserialize<List<SnippetEntry>>() ?? new();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("バックアップの辞書・定型文の部分を読み取れませんでした。");
        }

        DateTime? createdAt = DateTime.TryParse(ReadString(obj, "createdAt"), out var t) ? t : null;
        return new BackupContents(settings, dictionary, snippets, ReadString(obj, "appVersion"), createdAt);
    }

    /// <summary>読み取った内容で、設定・辞書・定型文をすべて置き換える。</summary>
    public void Apply(BackupContents contents)
    {
        _dictionary.RestoreFrom(contents.Dictionary);
        _snippets.RestoreFrom(contents.Snippets);
        // 設定は最後に置き換える（ホットキーやブラウザの再設定がここから始まるため）
        _settings.Restore(contents.Settings);
        _log.Info($"バックアップから復元しました（辞書 {contents.Dictionary.Count} 件・定型文 {contents.Snippets.Count} 件）");
    }

    private static string? ReadString(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>(); }
        catch { return null; }
    }

    private static int ReadInt(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }
}
