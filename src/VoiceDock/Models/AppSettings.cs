namespace VoiceDock.Models;

/// <summary>認識に使用するブラウザの選択方針。</summary>
public enum BrowserChoice
{
    /// <summary>Windows の既定ブラウザに合わせる（Chrome 系なら Chrome、それ以外は Edge）</summary>
    Auto,
    /// <summary>常に Microsoft Edge を使う</summary>
    Edge,
    /// <summary>常に Google Chrome を使う</summary>
    Chrome,
}

/// <summary>テキストの入力方式。</summary>
public enum InputMethod
{
    /// <summary>SendInput による直接キー入力（既定）</summary>
    SendInput,
    /// <summary>クリップボード経由の貼り付け (Ctrl+V)。直接入力を受け付けないアプリ向け</summary>
    Clipboard,
}

/// <summary>ホットキーの操作方式。</summary>
public enum HotkeyMode
{
    /// <summary>押すたびに録音開始/停止を切り替える（既定）</summary>
    Toggle,
    /// <summary>キーを押している間だけ録音する</summary>
    PushToTalk,
}

/// <summary>
/// アプリ設定。%APPDATA%\VoiceDock\settings.json に保存される。
/// </summary>
public class AppSettings
{
    /// <summary>録音開始/停止のトグルホットキー（例: "Ctrl+Space"）</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>ホットキーの操作方式（トグル / 押している間だけ録音）</summary>
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Toggle;

    /// <summary>直前に入力したテキストを取り消すホットキー</summary>
    public string UndoHotkey { get; set; } = "Ctrl+Shift+Space";

    /// <summary>直前の入力の取り消し（音声コマンド「とりけし」およびホットキー）を有効にする</summary>
    public bool UndoEnabled { get; set; } = true;

    /// <summary>Windows 起動時の自動起動</summary>
    public bool StartupEnabled { get; set; } = true;

    /// <summary>テキストの入力方式</summary>
    public InputMethod InputMethod { get; set; } = InputMethod.SendInput;

    /// <summary>
    /// アプリ（プロセス名）ごとの入力方式の上書き。
    /// ここに登録されたアプリが前面にある場合は、既定の入力方式より優先される。
    /// キーは拡張子なしのプロセス名（大文字小文字は区別しない）。
    /// </summary>
    public Dictionary<string, InputMethod> AppInputMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>認識に使用するブラウザ（既定は Windows の既定ブラウザに追従）</summary>
    public BrowserChoice Browser { get; set; } = BrowserChoice.Auto;

    /// <summary>
    /// 対応環境では端末内で音声認識を行う（音声をクラウドへ送信しない）。
    /// 非対応のブラウザ・言語パック未導入の場合は自動でクラウド認識にフォールバックする。
    /// </summary>
    public bool PreferLocalRecognition { get; set; }

    /// <summary>
    /// 音声コマンドを有効にする。改行・スペース等の操作、句読点（「まる」「てん」）、
    /// 記号（「アットマーク」等）の発話入力をまとめて制御する。
    /// </summary>
    public bool VoiceCommandsEnabled { get; set; } = true;

    /// <summary>定型文スニペット（「じゅうしょ」→ 住所全文 等）を有効にする</summary>
    public bool SnippetsEnabled { get; set; } = true;

    /// <summary>認識結果から日本語間の不要な半角スペースを除去する</summary>
    public bool RemoveSpaces { get; set; } = true;

    /// <summary>発話の区切りごとに文末へ「。」を自動挿入する</summary>
    public bool AutoPeriod { get; set; } = false;

    /// <summary>録音開始/停止時に操作音を鳴らす</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>初回起動ガイドを表示済みかどうか</summary>
    public bool FirstRunDone { get; set; }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        // 参照型は複製しないと、コピー元と同じ辞書を共有してしまう
        clone.AppInputMethods = new Dictionary<string, InputMethod>(AppInputMethods, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}
