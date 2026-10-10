namespace VoiceDock.Services;

/// <summary>
/// ダウンロード済みの新しい版へ、いま自動で入れ替えてよいかの判断。
///
/// 入れ替えると VoiceDock が数秒止まり、再起動する。その間はホットキーが効かず、開いている画面の
/// 入力途中の内容（辞書の登録欄など）も失われる。そのため、次のすべてを満たすときだけ入れ替える。
/// ・音声入力をしていない
/// ・最後に音声入力（開始・停止・文字の入力）をしてから 5 分以上たっている
/// ・PC の操作（キーボード・マウス）が 2 分以上無い（席を外しているなど、ホットキーを押す見込みが低い）
/// ・VoiceDock の画面（設定・辞書・更新画面など）を開いていない
/// </summary>
public static class IdleUpdatePolicy
{
    public static readonly TimeSpan SinceLastUse = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SinceUserInput = TimeSpan.FromMinutes(2);

    /// <summary>いま入れ替えてはいけない理由。入れ替えてよければ null。</summary>
    public static string? WhyNotNow(bool listening, TimeSpan sinceLastUse, TimeSpan sinceUserInput, bool windowOpen)
    {
        if (listening) return "音声入力中";
        if (windowOpen) return "VoiceDock の画面を開いている";
        if (sinceLastUse < SinceLastUse) return "少し前に音声入力をした";
        if (sinceUserInput < SinceUserInput) return "PC を操作している";
        return null;
    }
}
