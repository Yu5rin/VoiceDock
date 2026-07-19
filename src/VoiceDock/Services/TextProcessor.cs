using System.Text;
using System.Text.RegularExpressions;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>テキスト処理の結果。通常テキストか、音声コマンドかのどちらか。</summary>
public sealed record ProcessedText(string Text, bool IsCommand, string? CommandName = null);

/// <summary>
/// 認識結果テキストの後処理パイプライン。
/// 音声コマンド判定 → 辞書置換 → 不要スペース除去 → 文末句点の自動挿入 の順に適用する。
/// </summary>
public sealed class TextProcessor
{
    private readonly SettingsService _settings;
    private readonly DictionaryService _dictionary;

    /// <summary>
    /// 音声コマンド。発話全体がコマンド語と一致した場合のみ発動する
    /// （文中に含まれるだけでは反応しない）。
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Output)> Commands = new()
    {
        ["改行"] = ("改行", "\n"),
        ["かいぎょう"] = ("改行", "\n"),
        ["スペース"] = ("スペース", " "),
        ["タブ"] = ("タブ", "\t"),
    };

    /// <summary>日本語(非ASCII)文字に挟まれた半角スペースを除去する。</summary>
    private static readonly Regex JapaneseGapSpace =
        new(@"(?<=[^\x00-\x7F])[ 　]+(?=[^\x00-\x7F])", RegexOptions.Compiled);

    public TextProcessor(SettingsService settings, DictionaryService dictionary)
    {
        _settings = settings;
        _dictionary = dictionary;
    }

    /// <summary>確定した認識テキスト 1 区切り分を処理する。</summary>
    public ProcessedText Process(string raw)
    {
        var s = _settings.Current;
        var text = raw.Trim();
        if (text.Length == 0) return new ProcessedText("", false);

        // 音声コマンド（発話全体が一致した場合のみ）
        if (s.VoiceCommandsEnabled)
        {
            var key = text.TrimEnd('。', '、', '.', ',', '！', '!', '？', '?');
            if (Commands.TryGetValue(key, out var cmd))
                return new ProcessedText(cmd.Output, true, cmd.Name);
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

        return new ProcessedText(text, false);
    }
}
