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
}
