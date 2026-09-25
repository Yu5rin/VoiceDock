using System.Text.RegularExpressions;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>認識結果の処理種別。</summary>
public enum ProcessedKind
{
    /// <summary>通常のテキスト入力</summary>
    Text,
    /// <summary>音声コマンドによる入力（改行・句読点・記号など）</summary>
    Command,
    /// <summary>直前の入力の取り消し</summary>
    Undo,
}

/// <summary>テキスト処理の結果。</summary>
public sealed record ProcessedText(string Text, ProcessedKind Kind, string? CommandName = null);

/// <summary>
/// 認識結果テキストの後処理パイプライン。
/// 音声コマンド判定 → 定型文展開 → 辞書置換 → 不要スペース除去 → 文末句点の自動挿入
/// の順に適用する。
/// </summary>
public sealed class TextProcessor
{
    private readonly SettingsService _settings;
    private readonly DictionaryService _dictionary;
    private readonly SnippetService _snippets;

    /// <summary>
    /// 音声コマンド。発話全体がコマンド語と一致した場合のみ発動する
    /// （文中に含まれるだけでは反応しない）。
    /// 句読点・記号もこの仕組みに載せることで、「ドット」を含む単語などの誤爆を防ぐ。
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Output)> Commands = new()
    {
        // 操作
        ["改行"] = ("改行", "\n"),
        ["かいぎょう"] = ("改行", "\n"),
        ["スペース"] = ("スペース", " "),
        ["空白"] = ("スペース", " "),
        ["タブ"] = ("タブ", "\t"),

        // 句読点
        // 「まる」「てん」は単独で話すと漢字の「丸」「点」で認識されることが多い
        ["まる"] = ("句点", "。"),
        ["丸"] = ("句点", "。"),
        ["マル"] = ("句点", "。"),
        ["句点"] = ("句点", "。"),
        ["てん"] = ("読点", "、"),
        ["点"] = ("読点", "、"),
        ["テン"] = ("読点", "、"),
        ["読点"] = ("読点", "、"),
        ["びっくり"] = ("感嘆符", "！"),
        ["感嘆符"] = ("感嘆符", "！"),
        ["はてな"] = ("疑問符", "？"),
        ["疑問符"] = ("疑問符", "？"),
        ["中点"] = ("中点", "・"),
        ["なかぐろ"] = ("中点", "・"),
        ["中黒"] = ("中点", "・"),

        // 記号
        ["アットマーク"] = ("アットマーク", "@"),
        ["あっと"] = ("アットマーク", "@"),
        ["コロン"] = ("コロン", ":"),
        ["ころん"] = ("コロン", ":"),
        ["セミコロン"] = ("セミコロン", ";"),
        ["スラッシュ"] = ("スラッシュ", "/"),
        ["すらっしゅ"] = ("スラッシュ", "/"),
        ["ハイフン"] = ("ハイフン", "-"),
        ["はいふん"] = ("ハイフン", "-"),
        ["アンダーバー"] = ("アンダーバー", "_"),
        ["アンダースコア"] = ("アンダーバー", "_"),
        ["ドット"] = ("ドット", "."),
        ["ピリオド"] = ("ドット", "."),
        ["シャープ"] = ("シャープ", "#"),
        ["パーセント"] = ("パーセント", "%"),
        ["アンパサンド"] = ("アンパサンド", "&"),
        ["プラス"] = ("プラス", "+"),
        ["イコール"] = ("イコール", "="),
        ["アスタリスク"] = ("アスタリスク", "*"),
        ["かっこ"] = ("括弧", "（）"),
        ["括弧"] = ("括弧", "（）"),
        ["カッコ"] = ("括弧", "（）"),
        ["かぎかっこ"] = ("鉤括弧", "「」"),
        ["かぎ括弧"] = ("鉤括弧", "「」"),
        ["鉤括弧"] = ("鉤括弧", "「」"),
        ["カギカッコ"] = ("鉤括弧", "「」"),
        ["矢印"] = ("矢印", "→"),
        ["やじるし"] = ("矢印", "→"),
    };

    /// <summary>
    /// 英語で認識しているときの音声コマンド。日本語のコマンド語は英語の認識では
    /// 聞き取られないため、改行や句読点を英語で言えるようにする。
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Output)> EnglishCommands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["new line"] = ("改行", "\n"),
            ["newline"] = ("改行", "\n"),
            ["new paragraph"] = ("段落", "\n\n"),
            ["period"] = ("ピリオド", "."),
            ["full stop"] = ("ピリオド", "."),
            ["comma"] = ("カンマ", ","),
            ["question mark"] = ("疑問符", "?"),
            ["exclamation mark"] = ("感嘆符", "!"),
            ["exclamation point"] = ("感嘆符", "!"),
            ["colon"] = ("コロン", ":"),
            ["semicolon"] = ("セミコロン", ";"),
            ["at sign"] = ("アットマーク", "@"),
            ["hyphen"] = ("ハイフン", "-"),
            ["slash"] = ("スラッシュ", "/"),
        };

    /// <summary>英語で認識しているときの、取り消しのコマンド語。</summary>
    private static readonly HashSet<string> EnglishUndoWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "undo", "undo that", "scratch that", "delete that",
    };

    /// <summary>直前の入力を取り消すコマンド語。</summary>
    private static readonly HashSet<string> UndoWords = new()
    {
        "取り消し", "とりけし", "取消", "取り消して", "元に戻す", "もとにもどす",
    };

    /// <summary>
    /// 表記ゆれを吸収した照合用の表。「テン」「ﾃﾝ」も「てん」と同じコマンドとして扱う。
    /// 認識エンジンはコマンド語をひらがな・カタカナのどちらで返すか一定しないため。
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Output)> CommandLookup = BuildLookup(Commands);
    private static readonly Dictionary<string, (string Name, string Output)> EnglishCommandLookup = BuildLookup(EnglishCommands);
    private static readonly HashSet<string> UndoLookup = UndoWords.Select(KanaNormalizer.NormalizeKey).ToHashSet();
    private static readonly HashSet<string> EnglishUndoLookup = EnglishUndoWords.Select(KanaNormalizer.NormalizeKey).ToHashSet();

    private static Dictionary<string, (string Name, string Output)> BuildLookup(
        Dictionary<string, (string Name, string Output)> source)
    {
        var lookup = new Dictionary<string, (string Name, string Output)>(StringComparer.Ordinal);
        foreach (var (word, command) in source)
            lookup.TryAdd(KanaNormalizer.NormalizeKey(word), command);
        return lookup;
    }

    /// <summary>日本語(非ASCII)文字に挟まれた半角スペースを除去する。</summary>
    private static readonly Regex JapaneseGapSpace =
        new(@"(?<=[^\x00-\x7F])[ 　]+(?=[^\x00-\x7F])", RegexOptions.Compiled);

    public TextProcessor(SettingsService settings, DictionaryService dictionary, SnippetService snippets)
    {
        _settings = settings;
        _dictionary = dictionary;
        _snippets = snippets;
    }

    /// <summary>確定した認識テキスト 1 区切り分を処理する。</summary>
    public ProcessedText Process(string raw)
    {
        var s = _settings.Current;
        bool english = RecognitionLanguages.UsesWordSpacing(s.RecognitionLanguage);
        var text = raw.Trim();
        if (text.Length == 0) return new ProcessedText("", ProcessedKind.Text);

        // 発話全体が一致した場合のみ反応するもの（コマンド・取り消し・定型文）。
        // 認識結果の末尾に句読点が付くことがあるため、判定用に取り除く。
        var key = text.TrimEnd('。', '、', '.', ',', '！', '!', '？', '?');

        var lookupKey = KanaNormalizer.NormalizeKey(key);

        var undoWords = english ? EnglishUndoLookup : UndoLookup;
        if (s.UndoEnabled && undoWords.Contains(lookupKey))
            return new ProcessedText("", ProcessedKind.Undo, "取り消し");

        var commands = english ? EnglishCommandLookup : CommandLookup;
        if (s.VoiceCommandsEnabled && commands.TryGetValue(lookupKey, out var cmd))
            return new ProcessedText(cmd.Output, ProcessedKind.Command, cmd.Name);

        if (s.SnippetsEnabled)
        {
            var expanded = _snippets.TryExpand(key);
            if (expanded != null)
                return new ProcessedText(expanded, ProcessedKind.Command, $"定型文「{key}」");
        }

        // 辞書の「誤認識語→正しい語」強制置換
        text = _dictionary.ApplyReplacements(text);

        // 日本語の間に入る不要な半角スペースを除去（Web Speech API が挿入することがある）
        if (s.RemoveSpaces && !english)
            text = JapaneseGapSpace.Replace(text, "");

        // 文末句点の自動挿入（すでに句読点・記号で終わっている場合は付けない）
        if (s.AutoPeriod && text.Length > 0)
        {
            char last = text[^1];
            if (english)
            {
                if (!".,!?;:…)]\"'\n".Contains(last)) text += ".";
            }
            else if (!"。、．，!！?？…・「」）)]』】\n".Contains(last))
            {
                text += "。";
            }
        }

        return new ProcessedText(text, ProcessedKind.Text);
    }
}
