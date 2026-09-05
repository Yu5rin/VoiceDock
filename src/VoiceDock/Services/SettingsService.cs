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

    /// <summary>読み込み時に本体が壊れていて、控え (.bak) から復帰できた場合 true。</summary>
    public bool RecoveredFromBackup { get; private set; }

    /// <summary>本体も控えも読めず、初期設定で起動した場合 true（設定が失われた状態）。</summary>
    public bool ResetBecauseUnreadable { get; private set; }

    /// <summary>設定変更時に発火（引数は変更後の設定）</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>保存に失敗したときに発火（利用者へ知らせるため）。引数は理由の説明。</summary>
    public event Action<string>? SaveFailed;

    /// <summary>初期化したときに発火。ホットキー等を登録し直すために使う。</summary>
    public event Action? Reset;

    public SettingsService(LogService log)
    {
        _log = log;
    }

    public void Load()
    {
        lock (_sync)
        {
            // JSON として読めるかだけでなく、AppSettings へ変換できるかまで確かめる。
            // 型が合わない値が混じっている場合も「壊れている」と判定して .bak へ切り替える。
            var json = SafeFile.ReadAllText(AppPaths.SettingsFile, CanDeserialize, out bool fromBackup);
            if (json == null)
            {
                // ファイルが無い（初回起動）場合と、本体・控えとも壊れている場合がある
                if (System.IO.File.Exists(AppPaths.SettingsFile))
                {
                    _log.Error("設定ファイルが読み取れなかったため、初期設定で起動します");
                    ResetBecauseUnreadable = true;
                }
                Current = new AppSettings();
                return;
            }

            RecoveredFromBackup = fromBackup;
            if (fromBackup)
                _log.Warn("設定ファイルが壊れていたため、バックアップから復帰しました");

            try
            {
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                Normalize(Current);
            }
            catch (Exception ex)
            {
                // CanDeserialize を通っているので通常ここには来ない（保険）
                _log.Error($"設定ファイルの解釈に失敗したため、初期設定で起動します: {ex.Message}");
                Current = new AppSettings();
                RecoveredFromBackup = false;
                ResetBecauseUnreadable = true;
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
            Normalize(Current);
            Save();
            snapshot = Current;
        }
        _log.Info("設定を初期状態に戻しました");
        Changed?.Invoke(snapshot);
        Reset?.Invoke();
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

        // JSON から復元した辞書は、初期化子で指定した「大文字小文字を区別しない」比較を
        // 引き継がない。そのままだと Notepad と notepad が別扱いになり、
        // アプリ別の入力方式が効かなくなるため、毎回作り直す。
        var map = s.AppInputMethods;
        if (map is null)
        {
            s.AppInputMethods = new Dictionary<string, InputMethod>(StringComparer.OrdinalIgnoreCase);
        }
        else if (!ReferenceEquals(map.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            s.AppInputMethods = new Dictionary<string, InputMethod>(map, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>更新の確認先として許可する URL か（HTTPS かつ GitHub のみ）。</summary>
    public static bool IsAllowedUpdateUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>内容が AppSettings として読み込めるか（破損判定に使う）。</summary>
    private static bool CanDeserialize(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) != null;
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
            // 黙って失敗すると、利用者は変更が保存されたと思い込んでしまう
            _log.Error($"設定ファイルの保存に失敗しました: {ex.Message}");
            SaveFailed?.Invoke(UserMessage.Describe(ex));
        }
    }
}
