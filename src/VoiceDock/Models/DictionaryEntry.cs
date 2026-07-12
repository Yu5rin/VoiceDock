namespace VoiceDock.Models;

/// <summary>
/// 辞書の 1 エントリ。「誤認識語 → 正しい語」の対応。
/// 誤認識語が空のエントリは置換を行わず、initial_prompt のヒントとしてのみ使う。
/// </summary>
public class DictionaryEntry
{
    /// <summary>誤認識される語（置換元）</summary>
    public string Wrong { get; set; } = "";

    /// <summary>正しい語（置換先。initial_prompt のヒントにも使用）</summary>
    public string Correct { get; set; } = "";
}
