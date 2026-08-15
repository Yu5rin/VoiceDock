using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 定型文の管理画面。「読み → 展開後の本文」の 2 列テーブル形式。
/// 編集内容は即時保存する。
/// </summary>
public partial class SnippetWindow : Window
{
    private readonly SnippetService _snippets;
    private readonly ObservableCollection<SnippetEntry> _rows;

    public SnippetWindow(SnippetService snippets)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _snippets = snippets;
        _rows = new ObservableCollection<SnippetEntry>(snippets.Entries);
        Grid.ItemsSource = _rows;

        Grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(Persist);
    }

    private void DeleteRows_Click(object sender, RoutedEventArgs e)
    {
        var targets = Grid.SelectedItems.OfType<SnippetEntry>().ToList();
        foreach (var t in targets) _rows.Remove(t);
        Persist();
    }

    private void Persist() => _snippets.Replace(_rows);

    private void Window_Closing(object sender, CancelEventArgs e) => Persist();
}
