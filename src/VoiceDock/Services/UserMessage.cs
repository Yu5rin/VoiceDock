using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace VoiceDock.Services;

/// <summary>
/// 例外を、利用者に見せてよい短い日本語の説明へ変換する。
///
/// 例外の Message をそのまま画面に出すと、英文のスタックや内部パスがそのまま見えてしまい、
/// 何をすればよいか分からない。詳細はログへ残し、画面には「次に何をすべきか」を出す。
/// </summary>
public static class UserMessage
{
    /// <summary>原因に応じた短い説明を返す（末尾に句点を含む）。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "ファイルへのアクセスが許可されていません。保存先の権限をご確認ください。",
        FileNotFoundException => "ファイルが見つかりませんでした。",
        DirectoryNotFoundException => "保存先のフォルダが見つかりませんでした。",
        IOException => "ファイルの読み書きに失敗しました。他のアプリで開いていないかご確認ください。",
        HttpRequestException or SocketException => "ネットワークに接続できませんでした。接続状態をご確認ください。",
        TaskCanceledException or OperationCanceledException => "時間内に応答がありませんでした。",
        System.Text.Json.JsonException => "ファイルの形式が正しくありません。",
        _ => "予期しない問題が発生しました。詳細はログをご確認ください。",
    };
}
