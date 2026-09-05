using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Windows;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 更新の案内と適用を行う画面。
/// ダウンロード → SHA256 検証 → 入れ替え → 再起動 までを進捗表示つきで行う。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updater;
    private readonly UpdateInfo _info;
    private readonly SettingsService _settings;
    private readonly Action _shutdown;
    private bool _working;

    public UpdateWindow(UpdateService updater, UpdateInfo info, SettingsService settings, Action shutdown)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _updater = updater;
        _info = info;
        _settings = settings;
        _shutdown = shutdown;

        VersionText.Text = $"現在のバージョン {UpdateService.CurrentVersion} → 新しいバージョン {info.Version}";
        NotesText.Text = info.ReleaseNotes.Length > 0
            ? FormatReleaseNotes(info.ReleaseNotes)
            : "（リリースノートはありません）";

        // 書き込みできない場所（Program Files 等）に置かれている場合は自動更新できない
        if (!UpdateService.CanWriteToInstallDir(out var dir))
        {
            UpdateButton.IsEnabled = false;
            StatusText.Visibility = Visibility.Visible;
            StatusText.Text = $"このフォルダには書き込めないため、自動更新できません（{dir}）。" +
                              "リリースページから手動で差し替えてください。";
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        _working = true;

        UpdateButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        StatusText.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "ダウンロードしています…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p * 100;
                StatusText.Text = $"ダウンロードしています… {p * 100:F0}%";
            });

            var file = await _updater.DownloadAsync(_info, progress);

            StatusText.Text = "更新を適用しています…";
            Progress.IsIndeterminate = true;

            if (_updater.ApplyUpdate(file))
            {
                // 新しいバージョンが起動済み。こちらは速やかに終了する
                _shutdown();
                return;
            }

            StatusText.Text = "更新に失敗しました。元のバージョンのまま動作しています。";
            Progress.Visibility = Visibility.Collapsed;
            ToastWindow.Show("更新に失敗しました。リリースページから手動で差し替えてください。", ToastKind.Error);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"更新できませんでした。{UserMessage.Describe(ex)}";
            Progress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _working = false;
            UpdateButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            Progress.IsIndeterminate = false;
        }
    }

    private void ReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_info.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"ページを開けませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _settings.Update(s => s.SkippedVersion = _info.Version.ToString());
        ToastWindow.Show($"バージョン {_info.Version} は今後お知らせしません。手動での確認はいつでもできます。");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// リリースノートの Markdown を、そのまま読める平文へ整える。
    /// 「## 見出し」「- 箇条書き」「|表|」がそのまま出ると読みにくいため。
    /// </summary>
    private static string FormatReleaseNotes(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();

            // 表の区切り行（|---|---|）は読む意味がないので落とす
            if (line.StartsWith("|") && line.Trim('|', '-', ':', ' ').Length == 0) continue;

            if (line.StartsWith("#"))
            {
                var text = line.TrimStart('#').Trim();
                if (text.Length == 0) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("■ " + Inline(text));
                continue;
            }

            if (line.StartsWith(">"))
            {
                sb.AppendLine("  " + Inline(line.TrimStart('>').Trim()));
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                sb.AppendLine("・" + Inline(line[2..].Trim()));
                continue;
            }

            if (line.StartsWith("|"))
            {
                var cells = line.Trim('|').Split('|').Select(c => Inline(c.Trim()));
                sb.AppendLine("  " + string.Join(" / ", cells));
                continue;
            }

            sb.AppendLine(Inline(line));
        }
        return sb.ToString().Trim();
    }

    /// <summary>行内の Markdown 記法（**強調**・`コード`・[表示](URL)）を落とす。</summary>
    private static string Inline(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Replace("**", "").Replace("`", "");
    }
}
