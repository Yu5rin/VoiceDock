using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>
/// 設定の読み書き。%APPDATA%\VoiceDock\settings.json に JSON で保存する。
/// 設定画面からの変更は即時反映（即時保存）とする。
///
/// 保存は SafeFile 経由で原子的に行い、破損時は .bak から復帰する。
/// 直接上書きしていた頃は、書き込み中の異常終了で設定が失われていた。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly LogService _log;

    /// <summary>更新確認はバックグラウンドスレッドから設定を書くため、保存全体を直列化する。</summary>
    private readonly object _sync = new();

    public AppSettings Current { get; private set; } = new();

    /// <summary>読み込み時に破損が見つかり、バックアップから復帰した場合 true。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>設定変更時に発火（引数は変更後の設定）</summary>
    public event Action<AppSettings>? Changed;

    public SettingsService(LogService log)
    {
        _log = log;
    }

    public void Load()
    {
        lock (_sync)
        {
            var json = SafeFile.ReadAllText(AppPaths.SettingsFile, IsValidJson);
            if (json == null)
            {
                // ファイルが無い（初回起動）場合と、本体・バックアップとも壊れている場合がある
                if (System.IO.File.Exists(AppPaths.SettingsFile))
                {
                    _log.Error("設定ファイルが読み取れなかったため、初期設定で起動します");
                    RecoveredFromBackup = true;
                }
                Current = new AppSettings();
                return;
            }

            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Normalize(Current);
            }
            catch (Exception ex)
            {
                _log.Error($"設定ファイルの解釈に失敗したため、初期設定で起動します: {ex.Message}");
                Current = new AppSettings();
                RecoveredFromBackup = true;
            }
        }
    }

    /// <summary>設定を書き換えて即時保存する。</summary>
    public void Update(Action<AppSettings> mutate)
    {
        AppSettings snapshot;
        lock (_sync)
        {
            mutate(Current);
            Normalize(Current);
            Save();
            snapshot = Current;
        }
        Changed?.Invoke(snapshot);
    }

    /// <summary>設定を初期状態に戻す（辞書・定型文は消さない）。</summary>
    public void ResetToDefaults()
    {
        AppSettings snapshot;
        lock (_sync)
        {
            Current = new AppSettings();
            Save();
            snapshot = Current;
        }
        _log.Info("設定を初期状態に戻しました");
        Changed?.Invoke(snapshot);
    }

    /// <summary>
    /// 読み込んだ値のうち、不正だと危険なものを安全側へ補正する。
    /// 特に更新の確認先は、設定ファイルを書き換えられた場合に
    /// 任意の実行ファイルを取得させられないよう、GitHub の HTTPS に限定する。
    /// </summary>
    private void Normalize(AppSettings s)
    {
        if (!IsAllowedUpdateUrl(s.UpdateApiUrl))
        {
            _log.Warn($"更新の確認先が許可されていない URL のため、既定値に戻しました: {s.UpdateApiUrl}");
            s.UpdateApiUrl = new AppSettings().UpdateApiUrl;
        }
    }

    /// <summary>更新の確認先として許可する URL か（HTTPS かつ GitHub のみ）。</summary>
    public static bool IsAllowedUpdateUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureDirectories();
            SafeFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            _log.Error($"設定ファイルの保存に失敗しました: {ex.Message}");
        }
    }
}
