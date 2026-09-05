using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// ログ表示画面。エラー・動作ログ・認識結果テキストの履歴を一覧表示し、リアルタイムに追記する。
/// 種別フィルタと選択行のコピーに対応する。
/// </summary>
public partial class LogWindow : Window
{
    private readonly LogService _log;
    private readonly List<LogEntry> _entries = new();
    private string _filter = "すべて";

    public LogWindow(LogService log)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _log = log;

        foreach (var label in new[] { "すべて", "認識のみ", "エラー・警告のみ", "動作のみ" })
            FilterCombo.Items.Add(label);
        FilterCombo.SelectedIndex = 0;

        _entries.AddRange(log.Snapshot());
        Rebuild();

        _log.EntryAdded += OnEntryAdded;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _entries.Add(entry);
            if (Matches(entry))
            {
                LogList.Items.Add(entry.ToString());
                ScrollToEnd();
            }
            UpdateEmptyState();
        });
    }

    private bool Matches(LogEntry e) => _filter switch
    {
        "認識のみ" => e.Level == LogLevel.Recognition,
        "エラー・警告のみ" => e.Level is LogLevel.Error or LogLevel.Warn,
        "動作のみ" => e.Level == LogLevel.Info,
        _ => true,
    };

    private void Rebuild()
    {
        LogList.Items.Clear();
        foreach (var e in _entries.Where(Matches))
            LogList.Items.Add(e.ToString());
        UpdateEmptyState();
        ScrollToEnd();
    }

    private void UpdateEmptyState()
    {
        bool empty = LogList.Items.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _entries.Count == 0
            ? "まだログがありません。音声入力を使うとここに記録されます。"
            : "この種別に該当するログがありません。";
    }

    private void DeleteLogs_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "保存されているログファイルをすべて削除します。この操作は取り消せません。続けますか？",
            "ログの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        int deleted = _log.DeleteAllLogFiles();
        _log.ClearMemory();
        _entries.Clear();
        Rebuild();
        ToastWindow.Show($"ログを削除しました（{deleted} ファイル）。");
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is not string f) return;
        _filter = f;
        Rebuild();
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        var lines = LogList.SelectedItems.Count > 0
            ? LogList.SelectedItems.Cast<object>().Select(o => o.ToString())
            : LogList.Items.Cast<object>().Select(o => o.ToString());
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetDataObject(text, true);
            ToastWindow.Show(LogList.SelectedItems.Count > 0
                ? $"{LogList.SelectedItems.Count} 行をコピーしました。"
                : "表示中の全行をコピーしました。");
        }
        catch (Exception ex)
        {
            _log.Error($"ログのコピーに失敗しました: {ex}");
            ToastWindow.Show($"コピーできませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
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
            _log.Error($"ログフォルダを開けませんでした: {ex}");
            ToastWindow.Show($"フォルダを開けませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _log.EntryAdded -= OnEntryAdded;
    }
}
