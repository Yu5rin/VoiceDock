using System.Diagnostics;
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
    private readonly Action _shutdown;
    private bool _working;

    public UpdateWindow(UpdateService updater, UpdateInfo info, Action shutdown)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _updater = updater;
        _info = info;
        _shutdown = shutdown;

        VersionText.Text = $"現在のバージョン {UpdateService.CurrentVersion} → 新しいバージョン {info.Version}";
        NotesText.Text = info.ReleaseNotes.Length > 0 ? info.ReleaseNotes : "（リリースノートはありません）";

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
            StatusText.Text = $"更新に失敗しました: {ex.Message}";
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
            ToastWindow.Show($"ページを開けませんでした: {ex.Message}", ToastKind.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
