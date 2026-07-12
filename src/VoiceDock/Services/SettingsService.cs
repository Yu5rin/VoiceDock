using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// 設定の読み書き。%APPDATA%\VoiceDock\settings.json に JSON で保存する。
/// 設定画面からの変更は即時反映（即時保存）とする。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly LogService _log;

    public AppSettings Current { get; private set; } = new();

    /// <summary>設定変更時に発火（引数は変更後の設定）</summary>
    public event Action<AppSettings>? Changed;

    public SettingsService(LogService log)
    {
        _log = log;
    }

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"設定ファイルの読み込みに失敗しました: {ex.Message}");
            Current = new AppSettings();
        }
    }

    /// <summary>設定を書き換えて即時保存する。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
        Changed?.Invoke(Current);
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"設定ファイルの保存に失敗しました: {ex.Message}");
        }
    }
}
