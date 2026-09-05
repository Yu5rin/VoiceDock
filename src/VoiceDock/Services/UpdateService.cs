using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceDock.Models;

namespace VoiceDock.Services;

/// <summary>更新の確認結果。</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256,
    string ReleaseUrl,
    string ReleaseNotes);

/// <summary>
/// GitHub Releases を用いた更新の確認・ダウンロード・適用。
///
/// 通信は「起動時の確認」と「利用者が明示的に操作したとき」だけに限る。
/// 確認先 URL は設定ファイル (settings.json の UpdateApiUrl) に持たせており、
/// どこへ通信するのかが利用者から見えるようにしている。
///
/// 実行中の exe は Windows がロックされていて上書きできないが、リネームはできる。
/// この性質を使い「現行 exe を .old へ改名 → 新 exe を配置 → 新 exe を起動 → 自分は終了」
/// という手順で自己置換する。失敗時はリネームを元に戻す（ロールバック）。
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private const string OldSuffix = ".old";

    /// <summary>更新直後の再起動であることを新プロセスへ伝えるコマンドライン引数。</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>更新直後は、旧プロセスの終了を待ってから二重起動を判定する。</summary>
    public static readonly TimeSpan AfterUpdateWait = TimeSpan.FromSeconds(30);

    private readonly LogService _log;
    private readonly SettingsService _settings;

    /// <summary>確認中に再度押されても多重にリクエストしないためのフラグ。</summary>
    private int _checking;

    /// <summary>いま更新を確認中かどうか。</summary>
    public bool IsChecking => Volatile.Read(ref _checking) != 0;

    public UpdateService(LogService log, SettingsService settings)
    {
        _log = log;
        _settings = settings;
    }

    /// <summary>現在実行中のアプリのバージョン。</summary>
    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build)
            : new Version(0, 0, 0);

    /// <summary>
    /// 起動時の自動確認を行うべきかどうか（設定と前回確認日時から判断）。
    /// </summary>
    public bool ShouldCheckOnStartup()
    {
        var s = _settings.Current;
        return s.UpdateCheckMode switch
        {
            UpdateCheckMode.EveryStartup => true,
            UpdateCheckMode.DailyOnStartup =>
                s.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= CheckInterval,
            _ => false,
        };
    }

    /// <summary>
    /// 最新リリースを確認する。新しい版があればその情報を、無ければ null を返す。
    /// 通信エラーは握りつぶして null を返す（起動を妨げない）。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool ignoreSkipped = false, CancellationToken ct = default)
    {
        // 連打されても通信は 1 本に保つ
        if (Interlocked.Exchange(ref _checking, 1) != 0) return null;
        try
        {
            using var http = CreateClient(CheckTimeout);
            var url = _settings.Current.UpdateApiUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Warn("更新の確認先 URL が設定されていません");
                return null;
            }

            using var res = await http.GetAsync(url, ct);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(ct);

            _settings.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);

            var info = ParseRelease(json);
            if (info == null)
            {
                _log.Info("更新の確認: リリース情報を解釈できませんでした");
                return null;
            }

            // 比較は必ず数値として行う（文字列比較だと 1.0.10 < 1.0.9 と誤判定するため）
            if (info.Version <= CurrentVersion)
            {
                _log.Info($"更新の確認: 最新版を使用中です (現在 {CurrentVersion}, 最新 {info.Version})");
                return null;
            }

            // 利用者が「この版はスキップ」を選んでいる場合、自動確認では案内しない
            if (!ignoreSkipped && _settings.Current.SkippedVersion == info.Version.ToString())
            {
                _log.Info($"更新の確認: バージョン {info.Version} はスキップ指定されています");
                return null;
            }

            _log.Info($"更新の確認: 新しい版があります (現在 {CurrentVersion} → {info.Version})");
            return info;
        }
        catch (Exception ex)
        {
            // 更新が確認できなくても動作に支障はないため、警告に留める
            _log.Warn($"更新を確認できませんでした: {ex.Message}");
            return null;
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>リリース JSON から、Windows 向け exe アセットの情報を取り出す。</summary>
    private static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag)) return null;
        if (!TryParseVersion(tag, out var version)) return null;

        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(downloadUrl)) continue;
            // 応答が差し替えられた場合に、意図しない配布元から exe を取ってこないようにする
            if (!IsAllowedDownloadUrl(downloadUrl)) continue;

            long size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0;

            // GitHub の Release アセットには digest ("sha256:....") が付く場合がある
            string? sha = null;
            if (asset.TryGetProperty("digest", out var dg) && dg.GetString() is { } digest &&
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = digest["sha256:".Length..];
            }

            var releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            return new UpdateInfo(version, tag, downloadUrl, size, sha, releaseUrl, notes);
        }
        return null;
    }

    /// <summary>
    /// 更新ファイルの取得先として許可する URL か。
    /// HTTPS かつ GitHub のリリース配信ホストに限る。exe を取得して実行する処理のため、
    /// リリース JSON に書かれた URL をそのまま信用しない。
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"v0.6.2" のようなタグ名からバージョンを取り出す。</summary>
    private static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.TrimStart('v', 'V');
        // "0.6.2-beta" のような接尾辞を落とす
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out version!);
    }

    /// <summary>
    /// 更新ファイルを一時フォルダへダウンロードし、SHA256 を検証する。
    /// 検証に失敗した場合は例外を投げる（そのファイルは使わない）。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (!IsAllowedDownloadUrl(info.DownloadUrl))
            throw new InvalidOperationException("更新ファイルの取得先が許可されていない URL です。");

        Directory.CreateDirectory(TempDir);
        var path = Path.Combine(TempDir, $"VoiceDock-{info.TagName}.exe");

        try
        {
            using var http = CreateClient(DownloadTimeout);
            using var res = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? info.SizeBytes;

            await using var source = await res.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);

            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }
        catch
        {
            // 中断・失敗した場合、書きかけのファイルを残さない
            TryDelete(path);
            throw;
        }

        if (info.Sha256 is { Length: > 0 } expected)
        {
            // 数十 MB のハッシュ計算。UI スレッドで行うと画面が固まる
            var actual = await Task.Run(() => ComputeSha256(path), ct);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                throw new InvalidOperationException(
                    $"ダウンロードしたファイルの検証に失敗しました (SHA256 不一致)");
            }
            _log.Info("更新ファイルの SHA256 検証に成功しました");
        }
        else
        {
            _log.Warn("リリースに SHA256 が含まれていないため、ハッシュ検証を行いませんでした（通信は HTTPS で保護されています）");
        }

        return path;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// exe が置かれているフォルダに書き込み権限があるか。
    /// Program Files 等に置かれている場合は自動更新できない。
    /// </summary>
    public static bool CanWriteToInstallDir(out string dir)
    {
        dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (dir.Length == 0) return false;
        try
        {
            var probe = Path.Combine(dir, $".voicedock-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ダウンロード済みの exe を現行の exe と入れ替え、新しい方を起動する。
    /// 成功した場合、呼び出し元は速やかにアプリを終了すること。
    /// 途中で失敗した場合はリネームを元に戻し、false を返す。
    /// </summary>
    public bool ApplyUpdate(string downloadedExe)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrEmpty(current))
        {
            _log.Error("実行中の exe のパスを取得できませんでした");
            return false;
        }

        var backup = current + OldSuffix;
        bool renamed = false;

        try
        {
            // 前回の更新で残った .old があれば先に片付ける
            TryDelete(backup);

            // 実行中の exe は上書きできないが、リネームはできる
            File.Move(current, backup);
            renamed = true;

            File.Copy(downloadedExe, current, overwrite: true);

            // 旧プロセスがまだ終了しきっていないため、新プロセス側で二重起動判定を
            // 待つよう伝える（この引数が無いと「既に起動しています」で即終了してしまう）
            var psi = new ProcessStartInfo(current) { UseShellExecute = true };
            psi.ArgumentList.Add(AfterUpdateArgument);
            Process.Start(psi);

            // 入れ替えが済んだので、ダウンロードした一時ファイル（数十 MB）は不要
            TryDelete(downloadedExe);

            _log.Info("更新を適用し、新しいバージョンを起動しました");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"更新の適用に失敗しました: {ex.Message}");

            // ロールバック: 途中で失敗した場合はリネームを元に戻す
            if (renamed)
            {
                try
                {
                    TryDelete(current);
                    File.Move(backup, current);
                    _log.Info("更新に失敗したため、元のバージョンに戻しました");
                }
                catch (Exception rollbackEx)
                {
                    _log.Error($"元のバージョンへの復旧にも失敗しました: {rollbackEx.Message}。" +
                              $"{backup} を {current} に手動で戻してください");
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 前回の更新で残ったファイルを削除する（起動時に呼ぶ）。
    /// 対象は「入れ替え前の exe (.old)」と「ダウンロード用の一時フォルダ」。
    /// 更新に失敗した場合や途中で中断した場合、数十 MB の一時ファイルが残るため、
    /// 次の起動時に必ず片付ける。
    /// </summary>
    public void CleanupOldFiles()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(current))
            {
                var backup = current + OldSuffix;
                if (File.Exists(backup) && TryDelete(backup))
                    _log.Info("更新前のファイルを削除しました");
            }

            // 起動直後にダウンロード中ということはないため、まるごと削除してよい
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
                _log.Info("更新用の一時ファイルを削除しました");
            }
        }
        catch
        {
            // 掃除の失敗は無視（次回起動時に再試行される）
        }
    }

    /// <summary>更新ファイルのダウンロード先。</summary>
    private static string TempDir => Path.Combine(Path.GetTempPath(), "VoiceDockUpdate");

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>更新の確認（JSON 1 本）のタイムアウト。長すぎると起動直後に固まって見える。</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>更新ファイル（数十 MB）のダウンロードのタイムアウト。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub API は User-Agent を要求する
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("VoiceDock", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
