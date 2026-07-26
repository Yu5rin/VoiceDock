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

/// <summary>
/// アプリ設定。%APPDATA%\VoiceDock\settings.json に保存される。
/// </summary>
public class AppSettings
{
    /// <summary>録音開始/停止のトグルホットキー（例: "Ctrl+Space"）</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>Windows 起動時の自動起動</summary>
    public bool StartupEnabled { get; set; } = true;

    /// <summary>テキストの入力方式</summary>
    public InputMethod InputMethod { get; set; } = InputMethod.SendInput;

    /// <summary>認識に使用するブラウザ（既定は Windows の既定ブラウザに追従）</summary>
    public BrowserChoice Browser { get; set; } = BrowserChoice.Auto;

    /// <summary>音声コマンド（「改行」等の発話を操作に変換）を有効にする</summary>
    public bool VoiceCommandsEnabled { get; set; } = true;

    /// <summary>認識結果から日本語間の不要な半角スペースを除去する</summary>
    public bool RemoveSpaces { get; set; } = true;

    /// <summary>発話の区切りごとに文末へ「。」を自動挿入する</summary>
    public bool AutoPeriod { get; set; } = false;

    /// <summary>録音開始/停止時に操作音を鳴らす</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>初回起動ガイドを表示済みかどうか</summary>
    public bool FirstRunDone { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
