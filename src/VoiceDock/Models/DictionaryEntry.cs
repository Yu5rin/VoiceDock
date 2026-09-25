using System.Text.Json.Serialization;

namespace VoiceDock.Models;

/// <summary>
/// 辞書の 1 エントリ。「誤認識語 → 正しい語」の対応。
/// Web Speech API には認識前のヒントを渡す手段が無いため、
/// 辞書は認識後の強制置換にのみ使う。誤認識語が空の行は何も置換しない。
/// </summary>
public class DictionaryEntry
{
    /// <summary>誤認識される語（置換元）</summary>
    public string Wrong { get; set; } = "";

    /// <summary>正しい語（置換先）</summary>
    public string Correct { get; set; } = "";

    /// <summary>
    /// 発話全体が一致したときだけ置き換える。
    /// 辞書は文字列の一部でも置き換えるため、「角 → Kado」のような短い語は
    /// 「三角」「角度」の中にも当たってしまう。そうした語はこれを有効にする。
    /// </summary>
    public bool WholeOnly { get; set; }

    /// <summary>置き換えに使われた回数（「試す」欄での置き換えは数えない）。</summary>
    public int UseCount { get; set; }

    /// <summary>最後に置き換えに使われた日時。一度も使われていなければ null。</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>一覧に表示する一致のしかた。</summary>
    [JsonIgnore]
    public string MatchLabel => WholeOnly ? "全体のみ" : "部分でも";

    public DictionaryEntry Clone() => (DictionaryEntry)MemberwiseClone();
}
