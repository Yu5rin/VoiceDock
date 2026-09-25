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
    /// <summary>キー操作の音声コマンド（送信・全選択など）。文字ではなくキーを送る</summary>
    Keys,
}

/// <summary>送出するキー操作（修飾キー + キー）。VirtualKey は Windows の仮想キーコード。</summary>
public sealed record KeyStroke(ushort VirtualKey, bool Ctrl = false, bool Shift = false, bool Alt = false);

/// <summary>テキスト処理の結果。</summary>
public sealed record ProcessedText(string Text, ProcessedKind Kind, string? CommandName = null, KeyStroke? Keys = null);

/// <summary>
/// 認識結果テキストの後処理パイプライン。
/// つなぎ言葉の除去 → 取り消し・音声コマンド・キー操作・定型文の判定 → 英数字の幅そろえ
/// → 辞書置換 → 不要スペース除去 → 文末句点の自動挿入 の順に適用する。
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

    private const ushort VkBack = 0x08, VkReturn = 0x0D, VkA = 0x41, VkC = 0x43, VkV = 0x56, VkX = 0x58;

    /// <summary>
    /// キー操作の音声コマンド。改行は Shift+Enter で送るため、チャットで送信するには
    /// 手で Enter を押す必要があった。そうした操作を声で行えるようにする。
    /// 発話全体が一致したときだけ働く（「メールを送信した」のような文では働かない）。
    /// </summary>
    private static readonly Dictionary<string, (string Name, KeyStroke Keys)> KeyCommands = new()
    {
        ["送信"] = ("送信", new KeyStroke(VkReturn)),
        ["エンター"] = ("送信", new KeyStroke(VkReturn)),
        ["全選択"] = ("全選択", new KeyStroke(VkA, Ctrl: true)),
        ["全部選択"] = ("全選択", new KeyStroke(VkA, Ctrl: true)),
        ["すべて選択"] = ("全選択", new KeyStroke(VkA, Ctrl: true)),
        ["コピー"] = ("コピー", new KeyStroke(VkC, Ctrl: true)),
        ["切り取り"] = ("切り取り", new KeyStroke(VkX, Ctrl: true)),
        ["きりとり"] = ("切り取り", new KeyStroke(VkX, Ctrl: true)),
        ["貼り付け"] = ("貼り付け", new KeyStroke(VkV, Ctrl: true)),
        ["貼りつけ"] = ("貼り付け", new KeyStroke(VkV, Ctrl: true)),
        ["はりつけ"] = ("貼り付け", new KeyStroke(VkV, Ctrl: true)),
        ["ペースト"] = ("貼り付け", new KeyStroke(VkV, Ctrl: true)),
        ["一文字消す"] = ("一文字消す", new KeyStroke(VkBack)),
        ["1文字消す"] = ("一文字消す", new KeyStroke(VkBack)),
        ["一文字削除"] = ("一文字消す", new KeyStroke(VkBack)),
        ["バックスペース"] = ("一文字消す", new KeyStroke(VkBack)),
    };

    /// <summary>英語で認識しているときのキー操作コマンド。</summary>
    private static readonly Dictionary<string, (string Name, KeyStroke Keys)> EnglishKeyCommands = new()
    {
        ["press enter"] = ("送信", new KeyStroke(VkReturn)),
        ["send"] = ("送信", new KeyStroke(VkReturn)),
        ["select all"] = ("全選択", new KeyStroke(VkA, Ctrl: true)),
        ["copy"] = ("コピー", new KeyStroke(VkC, Ctrl: true)),
        ["cut"] = ("切り取り", new KeyStroke(VkX, Ctrl: true)),
        ["paste"] = ("貼り付け", new KeyStroke(VkV, Ctrl: true)),
        ["backspace"] = ("一文字消す", new KeyStroke(VkBack)),
    };

    /// <summary>
    /// 文頭や読点の後に現れる、つなぎ言葉（フィラー）。
    /// 「あの人」「その件」のように意味のある語を消さないよう、長音「ー」付きの形や
    /// 「えーと」「えっと」のように、つなぎ言葉としか使わない形だけを対象にする。
    /// 「えー」「あー」「んー」は、後ろが読点・空白・文末のときだけ取り除く。
    /// </summary>
    private static readonly Regex JapaneseFillers = new(
        @"(?:^|(?<=[、。！？!?\s]))(?:(?:えーっと|えーと|えっと|あのー+|そのー+|うーん|うーむ)|(?:えー+|あー+|んー+)(?=[、,\s]|$))[、,\s]*",
        RegexOptions.Compiled);

    /// <summary>英語のつなぎ言葉（um / uh など）。</summary>
    private static readonly Regex EnglishFillers = new(
        @"\b(?:um+|uh+|erm+|hmm+)\b,?\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    private static readonly Dictionary<string, (string Name, KeyStroke Keys)> KeyCommandLookup = BuildLookup(KeyCommands);
    private static readonly Dictionary<string, (string Name, KeyStroke Keys)> EnglishKeyCommandLookup = BuildLookup(EnglishKeyCommands);

    private static Dictionary<string, T> BuildLookup<T>(Dictionary<string, T> source)
    {
        var lookup = new Dictionary<string, T>(StringComparer.Ordinal);
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

        // つなぎ言葉は、コマンドの判定より先に取り除く（「えーと、送信」も送信として働くように）。
        // つなぎ言葉だけの発話は、何も入力しない
        if (s.RemoveFillers)
            text = (english ? EnglishFillers : JapaneseFillers).Replace(text, "").Trim();

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

        var keyCommands = english ? EnglishKeyCommandLookup : KeyCommandLookup;
        if (s.KeyCommandsEnabled && keyCommands.TryGetValue(lookupKey, out var keyCmd))
            return new ProcessedText("", ProcessedKind.Keys, keyCmd.Name, keyCmd.Keys);

        if (s.SnippetsEnabled)
        {
            var expanded = _snippets.TryExpand(key);
            if (expanded != null)
                return new ProcessedText(expanded, ProcessedKind.Command, $"定型文「{key}」");
        }

        // 英数字の幅をそろえる。辞書で登録した表記（例: 請求No.）はそのまま出したいため、
        // 辞書の置き換えより前に行う
        if (!english)
            text = ConvertWidth(text, s.CharacterWidth);

        // 辞書の「誤認識語→正しい語」強制置換
        text = _dictionary.ApplyReplacements(text, countUsage: true);

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

    /// <summary>英数字（0-9, A-Z, a-z）を半角または全角にそろえる。記号は変えない。</summary>
    private static string ConvertWidth(string text, CharacterWidth width)
    {
        if (width == CharacterWidth.AsIs) return text;
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (width == CharacterWidth.Half)
            {
                // 全角英数（０-９, Ａ-Ｚ, ａ-ｚ）は、半角との差が 0xFEE0 で一定
                if (c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
                    chars[i] = (char)(c - 0xFEE0);
            }
            else if (c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                chars[i] = (char)(c + 0xFEE0);
            }
        }
        return new string(chars);
    }
}
