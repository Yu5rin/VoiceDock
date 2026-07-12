namespace VoiceDock.Models;

/// <summary>
/// アプリ設定。%APPDATA%\VoiceDock\settings.json に保存される。
/// </summary>
public class AppSettings
{
    /// <summary>録音開始/停止のトグルホットキー（例: "Ctrl+Alt+Space"）</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Whisper モデルサイズ（tiny / base / small / medium / large-v3）</summary>
    public string ModelSize { get; set; } = "medium";

    /// <summary>使用するマイクのデバイス名。null または空なら既定のデバイス</summary>
    public string? MicDeviceName { get; set; }

    /// <summary>Windows 起動時の自動起動</summary>
    public bool StartupEnabled { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
