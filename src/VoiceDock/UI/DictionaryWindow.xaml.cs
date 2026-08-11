using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 辞書管理画面。「誤認識語・正しい語」の 2 列テーブル形式で、
/// Excel からのセル範囲貼り付け（Ctrl+V）と CSV エクスポート/インポートに対応する。
/// </summary>
public partial class DictionaryWindow : Window
{
    private readonly DictionaryService _dictionary;
    private readonly ObservableCollection<DictionaryEntry> _rows;

    public DictionaryWindow(DictionaryService dictionary)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _dictionary = dictionary;
        _rows = new ObservableCollection<DictionaryEntry>(dictionary.Entries);
        Grid.ItemsSource = _rows;

        Grid.PreviewKeyDown += Grid_PreviewKeyDown;
        Grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(Persist);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            PasteFromClipboard();
        }
        else if (e.Key == Key.Delete && Grid.SelectedItems.Count > 0 && !IsEditing())
        {
            e.Handled = true;
            DeleteSelectedRows();
        }
    }

    private bool IsEditing() =>
        Grid.CurrentCell.IsValid &&
        (Grid.CurrentColumn?.GetCellContent(Grid.CurrentCell.Item) as FrameworkElement)?.Parent is System.Windows.Controls.DataGridCell { IsEditing: true };

    /// <summary>
    /// クリップボードのタブ区切りテキスト（Excel のセル範囲コピー形式）を
    /// 「誤認識語・正しい語」の行として追加する。
    /// </summary>
    private void PasteFromClipboard()
    {
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        int added = 0;

        foreach (var lineRaw in text.Split('\n'))
        {
            var line = lineRaw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var cells = line.Split('\t');
            var wrong = cells.ElementAtOrDefault(0)?.Trim() ?? "";
            var correct = cells.ElementAtOrDefault(1)?.Trim() ?? "";
            if (wrong.Length == 0 && correct.Length == 0) continue;
            _rows.Add(new DictionaryEntry { Wrong = wrong, Correct = correct });
            added++;
        }

        if (added > 0)
        {
            Persist();
            ToastWindow.Show($"{added} 件を貼り付けました。");
        }
    }

    private void DeleteRows_Click(object sender, RoutedEventArgs e) => DeleteSelectedRows();

    private void DeleteSelectedRows()
    {
        var targets = Grid.SelectedCells
            .Select(c => c.Item)
            .OfType<DictionaryEntry>()
            .Distinct()
            .ToList();
        foreach (var t in targets) _rows.Remove(t);
        if (targets.Count > 0) Persist();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Persist();
        var dialog = new SaveFileDialog
        {
            Title = "辞書のエクスポート",
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = "voicedock-dictionary.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _dictionary.ExportCsv(dialog.FileName);
            ToastWindow.Show("辞書を CSV にエクスポートしました。ファイルには社内用語・人名が含まれるため取り扱いに注意してください。", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"エクスポートに失敗しました: {ex.Message}", ToastKind.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "辞書のインポート",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            int count = _dictionary.ImportCsv(dialog.FileName);
            _rows.Clear();
            foreach (var entry in _dictionary.Entries) _rows.Add(entry);
            ToastWindow.Show($"辞書を CSV からインポートしました ({count} 件)。既存の内容は置き換えられました。");
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"インポートに失敗しました: {ex.Message}", ToastKind.Error);
        }
    }

    private void Persist() => _dictionary.Replace(_rows);

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        Grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        Persist();
    }
}
