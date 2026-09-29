using System.Diagnostics;
using System.IO;
using System.Net;
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
    /// 直近の確認が失敗した理由（利用者向けの文）。成功した場合や最新版だった場合は null。
    /// 手動で確認したときに「最新です」と誤って伝えないために使う。
    /// </summary>
    public string? LastCheckError { get; private set; }

    /// <summary>
    /// 最新リリースを確認する。新しい版があればその情報を、無ければ null を返す。
    /// 通信エラーは握りつぶして null を返す（起動を妨げない）。失敗した理由は LastCheckError に残す。
    ///
    /// 手順:
    /// 1. リリース一覧（Atom）で最新の版を調べる。API の回数上限の対象外なので、会社の回線でも届く。
    ///    最新版を使っていれば、ここで終わる（API は使わない）
    /// 2. 新しい版があれば、API でリリースの詳細（添付ファイルと SHA256）を取る
    /// 3. API が上限などで使えなければ、ダウンロード先を名前の規則から組み立てて続ける（SHA256 の照合は省く）
    /// Atom が読めない場合は、これまでどおり API だけで確認する。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(bool ignoreSkipped = false, CancellationToken ct = default)
    {
        // 連打されても通信は 1 本に保つ
        if (Interlocked.Exchange(ref _checking, 1) != 0) return null;
        LastCheckError = null;
        try
        {
            var url = _settings.Current.UpdateApiUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log.Warn("更新の確認先 URL が設定されていません");
                LastCheckError = "更新の確認先が設定されていません。";
                return null;
            }

            using var http = CreateClient(CheckTimeout);

            // 1. リリース一覧（Atom）で最新の版を調べる
            FeedRelease? feed = null;
            if (UpdateFeed.TryBuildAtomUrl(url) is { } atomUrl)
            {
                try
                {
                    // 既定の Accept（API の JSON）では意図が合わないので、この要求にだけ Atom 用を付ける
                    using var req = new HttpRequestMessage(HttpMethod.Get, atomUrl);
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));
                    using var res = await http.SendAsync(req, ct);
                    res.EnsureSuccessStatusCode();
                    feed = UpdateFeed.ParseAtom(await res.Content.ReadAsStringAsync(ct));
                    if (feed == null) _log.Info("更新の確認: リリース一覧（Atom）に版が見つかりませんでした");
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _log.Warn($"更新の確認: リリース一覧（Atom）を読めませんでした: {ex.Message}");
                }
            }

            if (feed != null)
            {
                _settings.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
                if (!IsNewAndNotSkipped(feed.Version, ignoreSkipped)) return null;
            }

            // 2. API でリリースの詳細を取る
            UpdateInfo? info = null;
            string? apiError = null;
            try
            {
                using var res = await http.GetAsync(url, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadAsStringAsync(ct);
                _settings.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
                info = ParseRelease(json);
                if (info == null)
                {
                    _log.Info("更新の確認: リリース情報を解釈できませんでした");
                    LastCheckError = "リリース情報を読み取れませんでした。リリースページをご確認ください。";
                    return null;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                _log.Warn($"更新の確認: 配布元（GitHub API）への問い合わせが回数の上限に達していました ({ex.Message})");
                apiError = RateLimitMessage;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.Warn($"更新を確認できませんでした: {ex.Message}");
                apiError = $"更新を確認できませんでした。{UserMessage.Describe(ex)}";
            }

            // 3. API が使えなければ、Atom で分かった版のダウンロード先を組み立てる
            if (info == null)
            {
                var built = feed == null ? null : UpdateFeed.TryBuildDownloadUrl(url, feed.TagName);
                if (feed == null || built == null)
                {
                    LastCheckError = apiError;
                    return null;
                }
                _log.Info($"更新の確認: API が使えなかったため、ダウンロード先を組み立てて続けます（SHA256 の照合は省きます）: {built}");
                info = new UpdateInfo(feed.Version, feed.TagName, built, 0, null, feed.ReleaseUrl, feed.Notes);
            }

            // Atom で新しい版と判定済みで、API も同じ版を返したなら、もう一度判定しない（ログが二重になる）
            if (feed != null && info.Version == feed.Version) return info;
            return IsNewAndNotSkipped(info.Version, ignoreSkipped) ? info : null;
        }
        catch (Exception ex)
        {
            // 更新が確認できなくても動作に支障はないため、警告に留める
            _log.Warn($"更新を確認できませんでした: {ex.Message}");
            LastCheckError = $"更新を確認できませんでした。{UserMessage.Describe(ex)}";
            return null;
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>API の回数上限に当たったときの説明。自分の操作が原因ではないことが分かるようにする。</summary>
    private const string RateLimitMessage =
        "配布元（GitHub）への問い合わせが回数の上限に達していました。この上限は同じ回線を使う人たちで共有されるため、" +
        "会社などでは自分が何度も確認していなくても起こります。しばらくおいてから確認するか、リリースページから直接ダウンロードしてください。";

    /// <summary>新しい版で、かつ（自動確認では）スキップ指定されていないか。結果をログに残す。</summary>
    private bool IsNewAndNotSkipped(Version latest, bool ignoreSkipped)
    {
        // 比較は必ず数値として行う（文字列比較だと 1.0.10 < 1.0.9 と誤判定するため）
        if (latest <= CurrentVersion)
        {
            _log.Info($"更新の確認: 最新版を使用中です (現在 {CurrentVersion}, 最新 {latest})");
            return false;
        }

        // 利用者が「この版はスキップ」を選んでいる場合、自動確認では案内しない
        if (!ignoreSkipped && _settings.Current.SkippedVersion == latest.ToString())
        {
            _log.Info($"更新の確認: バージョン {latest} はスキップ指定されています");
            return false;
        }

        _log.Info($"更新の確認: 新しい版があります (現在 {CurrentVersion} → {latest})");
        return true;
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
    private static bool TryParseVersion(string tag, out Version version) => UpdateFeed.TryParseVersion(tag, out version);

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

        // 失敗した理由を後から追えるよう、取得先と結果をログに残す（以前は画面にしか出ず、原因が分からなかった）
        _log.Info($"更新ファイルをダウンロードします: {info.DownloadUrl}");
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
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                _log.Info("更新ファイルのダウンロードを中止しました");
            else
                _log.Warn($"更新ファイルをダウンロードできませんでした: {ex.GetType().Name}: {ex.Message}");
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
                _log.Warn($"更新ファイルの SHA256 が一致しませんでした (期待 {expected} / 実際 {actual})");
                TryDelete(path);
                throw new InvalidOperationException(
                    $"ダウンロードしたファイルの検証に失敗しました (SHA256 不一致)");
            }
            _log.Info("更新ファイルの SHA256 検証に成功しました");
        }
        else
        {
            _log.Warn("SHA256 が分からないため、ハッシュ検証を行いませんでした（通信は HTTPS で保護されています）");
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

    /// <summary>
    /// テストで通信を差し替えるための口（回数上限などの応答を再現して、確認の流れを試すため）。
    /// アプリの動作では常に null。
    /// </summary>
    internal static HttpMessageHandler? TestHandler;

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = TestHandler != null
            ? new HttpClient(TestHandler, disposeHandler: false) { Timeout = timeout }
            : new HttpClient { Timeout = timeout };
        // GitHub API は User-Agent を要求する
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("VoiceDock", CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
