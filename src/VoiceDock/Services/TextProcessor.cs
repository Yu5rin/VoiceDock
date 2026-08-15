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
        ["まる"] = ("句点", "。"),
        ["マル"] = ("句点", "。"),
        ["句点"] = ("句点", "。"),
        ["てん"] = ("読点", "、"),
        ["テン"] = ("読点", "、"),
        ["読点"] = ("読点", "、"),
        ["びっくり"] = ("感嘆符", "！"),
        ["感嘆符"] = ("感嘆符", "！"),
        ["はてな"] = ("疑問符", "？"),
        ["疑問符"] = ("疑問符", "？"),
        ["中点"] = ("中点", "・"),
        ["なかぐろ"] = ("中点", "・"),

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
        ["カッコ"] = ("括弧", "（）"),
        ["かぎかっこ"] = ("鉤括弧", "「」"),
        ["カギカッコ"] = ("鉤括弧", "「」"),
        ["矢印"] = ("矢印", "→"),
        ["やじるし"] = ("矢印", "→"),
    };

    /// <summary>直前の入力を取り消すコマンド語。</summary>
    private static readonly HashSet<string> UndoWords = new()
    {
        "取り消し", "とりけし", "取消", "取り消して", "元に戻す", "もとにもどす",
    };

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
        var text = raw.Trim();
        if (text.Length == 0) return new ProcessedText("", ProcessedKind.Text);

        // 発話全体が一致した場合のみ反応するもの（コマンド・取り消し・定型文）。
        // 認識結果の末尾に句読点が付くことがあるため、判定用に取り除く。
        var key = text.TrimEnd('。', '、', '.', ',', '！', '!', '？', '?');

        if (s.UndoEnabled && UndoWords.Contains(key))
            return new ProcessedText("", ProcessedKind.Undo, "取り消し");

        if (s.VoiceCommandsEnabled && Commands.TryGetValue(key, out var cmd))
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
        if (s.RemoveSpaces)
            text = JapaneseGapSpace.Replace(text, "");

        // 文末句点の自動挿入（すでに句読点・記号で終わっている場合は付けない）
        if (s.AutoPeriod && text.Length > 0)
        {
            char last = text[^1];
            if (!"。、．，!！?？…・「」）)]』】\n".Contains(last))
                text += "。";
        }

        return new ProcessedText(text, ProcessedKind.Text);
    }
}
