namespace VoiceDock.Models;

/// <summary>
/// アプリ設定。%APPDATA%\VoiceDock\settings.json に保存される。
/// </summary>
public class AppSettings
{
    /// <summary>録音開始/停止のトグルホットキー（例: "Ctrl+Space"）</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>Windows 起動時の自動起動</summary>
    public bool StartupEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
