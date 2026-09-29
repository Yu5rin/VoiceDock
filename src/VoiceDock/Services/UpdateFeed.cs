using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VoiceDock.Services;

/// <summary>リリース一覧（Atom）から読み取った、いちばん新しい版。</summary>
public sealed record FeedRelease(Version Version, string TagName, string ReleaseUrl, string Notes);

/// <summary>
/// GitHub の API を使わずに、更新の有無とダウンロード先を知るための処理。
///
/// 【なぜ要るか】
/// GitHub の API（api.github.com）には、未認証だと 1 時間 60 回の上限があり、IP アドレスごとに数えられる。
/// 会社のように大勢が同じ回線（同じ IP）を使う場所では、ほかの人の通信で先に使い切られてしまい、
/// 更新の確認が「403 rate limit exceeded」で失敗し続ける（実機ログ 2026-09-29）。
/// 一方、リリース一覧の Atom フィード（github.com/{owner}/{repo}/releases.atom）はこの上限の対象外で、
/// 配布ファイルの URL も名前の付け方が決まっているため組み立てられる。
///
/// 確認先は、設定の UpdateApiUrl（利用者から見える唯一の確認先）から導く。別の URL は持たない。
/// </summary>
public static class UpdateFeed
{
    private static readonly Regex ApiLatestUrl = new(
        @"^https://api\.github\.com/repos/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/releases/latest/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 添付する exe の名前。Actions のリリース（.github/workflows/build.yml）も、自分でビルドして
    /// リリースする手順も、この名前で添付する。変えるならここも直すこと。
    /// </summary>
    public static string AssetFileName(string tag) => $"VoiceDock-{tag}-win-x64.exe";

    /// <summary>API の確認先から、リリース一覧（Atom）の URL を導く。導けなければ null。</summary>
    public static string? TryBuildAtomUrl(string? apiUrl) =>
        TryGetRepo(apiUrl, out var owner, out var repo)
            ? $"https://github.com/{owner}/{repo}/releases.atom"
            : null;

    /// <summary>
    /// API を使わずに、配布ファイルのダウンロード URL を組み立てる。組み立てられなければ null。
    /// 名前が実際の添付と違っていれば 404 でダウンロードに失敗するだけで、別のファイルが入ることはない。
    /// </summary>
    public static string? TryBuildDownloadUrl(string? apiUrl, string tag)
    {
        if (string.IsNullOrEmpty(tag) || !TryGetRepo(apiUrl, out var owner, out var repo)) return null;
        return $"https://github.com/{owner}/{repo}/releases/download/" +
               $"{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(AssetFileName(tag))}";
    }

    private static bool TryGetRepo(string? apiUrl, out string owner, out string repo)
    {
        owner = repo = "";
        if (string.IsNullOrWhiteSpace(apiUrl)) return false;
        var m = ApiLatestUrl.Match(apiUrl.Trim());
        if (!m.Success) return false;
        owner = m.Groups["owner"].Value;
        repo = m.Groups["repo"].Value;
        return true;
    }

    /// <summary>
    /// リリース一覧（Atom）から、いちばん新しい版を取り出す。読めなければ null。
    /// 並び順に頼らず、タグの版番号がいちばん大きいものを選ぶ。
    /// 下書きは Atom に出ない。プレリリースは区別できないが、このアプリでは使っていない。
    /// </summary>
    public static FeedRelease? ParseAtom(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        FeedRelease? best = null;
        foreach (var entry in doc.Descendants(atom + "entry"))
        {
            var href = entry.Elements(atom + "link")
                .Select(l => (string?)l.Attribute("href"))
                .FirstOrDefault(h => h != null && h.Contains("/releases/tag/", StringComparison.Ordinal));
            if (href == null) continue;

            var tag = Uri.UnescapeDataString(href[(href.LastIndexOf("/releases/tag/", StringComparison.Ordinal) + "/releases/tag/".Length)..]);
            if (!TryParseVersion(tag, out var version)) continue;
            if (best != null && version <= best.Version) continue;

            var notes = HtmlToText((string?)entry.Element(atom + "content") ?? "");
            best = new FeedRelease(version, tag, href, notes);
        }
        return best;
    }

    /// <summary>"v0.6.2" のようなタグ名からバージョンを取り出す。</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.TrimStart('v', 'V');
        // "0.6.2-beta" のような接尾辞を落とす
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out version!);
    }

    /// <summary>
    /// Atom の本文（リリース本文を HTML にしたもの）を、更新画面で読める形に戻す。
    /// 見出し・箇条書き・表は Markdown に近い形にし、更新画面の整形（UpdateWindow）にそのまま渡せるようにする。
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Replace("\r", "");
        // GitHub の HTML は表のタグごとに改行が入るため、表の中だけ空白を詰めてから 1 行ずつにする
        s = Regex.Replace(s, @"\s*(</?(?:table|thead|tbody|tr|td|th)\b[^>]*>)\s*", "$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<h([1-6])[^>]*>", m => "\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<tr[^>]*>", "\n| ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]>", " | ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</h[1-6]>|</li>|</table>|</ul>|</ol>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);

        var sb = new StringBuilder();
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            // 表の見出しと中身の間に出る空行や、HTML の字下げでできた空行はまとめる
            if (t.Length == 0 && (sb.Length == 0 || sb.ToString().EndsWith("\n\n", StringComparison.Ordinal))) continue;
            sb.Append(t).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
