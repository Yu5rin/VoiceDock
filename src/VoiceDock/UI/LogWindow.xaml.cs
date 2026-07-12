using System.Diagnostics;
using System.Windows;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// ログ表示画面。エラー・動作ログ・認識結果テキストの履歴を一覧表示し、リアルタイムに追記する。
/// </summary>
public partial class LogWindow : Window
{
    private readonly LogService _log;

    public LogWindow(LogService log)
    {
        InitializeComponent();
        _log = log;

        foreach (var entry in log.Snapshot())
            LogList.Items.Add(entry.ToString());
        ScrollToEnd();

        _log.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(entry.ToString());
            ScrollToEnd();
        });
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"フォルダを開けませんでした: {ex.Message}", ToastKind.Error);
        }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _log.EntryAdded -= OnEntryAdded;
    }
}
