namespace VoiceDock.Models;

/// <summary>
/// 定型文スニペットの 1 エントリ。「読み（発話）→ 展開後テキスト」の対応。
/// 例: 「じゅうしょ」→「東京都千代田区…」
/// </summary>
public class SnippetEntry
{
    /// <summary>発話する読み（この語だけを発話したときに展開される）</summary>
    public string Phrase { get; set; } = "";

    /// <summary>実際に入力される展開後のテキスト</summary>
    public string Expansion { get; set; } = "";
}
