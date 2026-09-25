using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 定型文の管理画面。上のフォームで 1 件ずつ登録・編集し、下の一覧で確認・検索・削除する。
///
/// 以前は表のセルを直接編集する作りで、本文が 1 行の入力欄だったため、
/// 住所や署名のような改行を含む本文がまともに登録できなかった。
/// 本文は複数行の入力欄にしている。変更は操作のたびに即時保存する。
/// </summary>
public partial class SnippetWindow : Window
{
    private readonly SnippetService _snippets;
    private readonly LogService _log;
    private readonly ObservableCollection<SnippetEntry> _rows;
    private readonly ICollectionView _view;

    /// <summary>フォームで編集中の項目。null なら新規登録の状態。</summary>
    private SnippetEntry? _editing;

    /// <summary>フォームへ読み込んでいる最中は、選択変更などの連鎖を無視する。</summary>
    private bool _loadingForm;

    public SnippetWindow(SnippetService snippets, LogService log)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _snippets = snippets;
        _log = log;

        _rows = new ObservableCollection<SnippetEntry>(snippets.Entries);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is SnippetEntry e && MatchesSearch(e);
        Grid.ItemsSource = _view;
        _rows.CollectionChanged += (_, _) => UpdateListState();

        PhraseBox.KeyDown += PhraseBox_KeyDown;
        ExpansionBox.PreviewKeyDown += ExpansionBox_PreviewKeyDown;
        Grid.PreviewKeyDown += Grid_PreviewKeyDown;
        PreviewKeyDown += Window_PreviewKeyDown;

        ClearForm();
        UpdateListState();
        Loaded += (_, _) => PhraseBox.Focus();
    }

    // ───────────── フォーム ─────────────

    private void PhraseBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        // 本文は改行を含められるため、読みの欄の Enter は本文の欄へ進むだけにする
        ExpansionBox.Focus();
        ExpansionBox.CaretIndex = ExpansionBox.Text.Length;
    }

    private void ExpansionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 本文の欄では Enter が改行になるため、登録は Ctrl+Enter にする
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Submit();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Grid.UnselectAll();
        ClearForm();
        PhraseBox.Focus();
    }

    /// <summary>フォームの内容で追加（新規）または更新（編集中）する。</summary>
    private void Submit()
    {
        var phrase = PhraseBox.Text.Trim();
        // 本文は前後の空白や改行も意図したものである可能性があるため、そのまま使う。
        // ただし改行コードは LF にそろえる
        var expansion = ExpansionBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (phrase.Length == 0)
        {
            FormStatus.Text = "読み（発話）を入力してください。";
            PhraseBox.Focus();
            return;
        }
        if (expansion.Trim().Length == 0)
        {
            FormStatus.Text = "入力する本文を入力してください。";
            ExpansionBox.Focus();
            return;
        }

        // 照合はひらがな・カタカナを区別しないため、重複もその基準で判定する
        var key = KanaNormalizer.NormalizeKey(phrase);
        var duplicate = _rows.FirstOrDefault(r => !ReferenceEquals(r, _editing) &&
                                                  KanaNormalizer.NormalizeKey(r.Phrase) == key);
        if (duplicate != null)
        {
            var answer = MessageBox.Show(this,
                $"読み「{duplicate.Phrase}」は既に登録されています。\n" +
                "（ひらがな・カタカナの違いは同じ語として扱います）\n\n" +
                "今回入力した本文に置き換えますか？",
                "同じ読みの登録があります", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _rows.Remove(duplicate);
        }

        var entry = new SnippetEntry { Phrase = phrase, Expansion = expansion };
        bool updated = _editing != null;
        if (_editing != null && _rows.IndexOf(_editing) is var index and >= 0)
            _rows[index] = entry;
        else
            _rows.Add(entry);

        Persist();
        Grid.UnselectAll();
        ClearForm();
        Grid.ScrollIntoView(entry);
        FormStatus.Text = updated || duplicate != null
            ? $"「{phrase}」を更新しました。"
            : $"「{phrase}」を登録しました。";
        PhraseBox.Focus();
    }

    private void ClearForm()
    {
        _loadingForm = true;
        _editing = null;
        PhraseBox.Text = "";
        ExpansionBox.Text = "";
        PrimaryButton.Content = "追加";
        FormStatus.Text = "";
        _loadingForm = false;
    }

    private void LoadIntoForm(SnippetEntry entry)
    {
        _loadingForm = true;
        _editing = entry;
        PhraseBox.Text = entry.Phrase;
        ExpansionBox.Text = entry.Expansion;
        PrimaryButton.Content = "更新";
        FormStatus.Text = "選択した項目を編集しています。［新規入力］で新しく登録する状態に戻ります。";
        _loadingForm = false;
    }

    /// <summary>フォームに、まだ登録していない入力が残っているか。</summary>
    private bool HasUnsavedInput()
    {
        var phrase = PhraseBox.Text.Trim();
        var expansion = ExpansionBox.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (_editing == null) return phrase.Length > 0 || expansion.Trim().Length > 0;
        return phrase != _editing.Phrase || expansion != _editing.Expansion;
    }

    // ───────────── 一覧 ─────────────

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingForm) return;
        if (Grid.SelectedItems.Count == 1 && Grid.SelectedItem is SnippetEntry entry)
            LoadIntoForm(entry);
        else if (Grid.SelectedItems.Count == 0 && _editing != null)
            ClearForm();
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            DeleteSelectedRows();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateListState();
    }

    private bool MatchesSearch(SnippetEntry entry)
    {
        var query = KanaNormalizer.NormalizeKey(SearchBox?.Text);
        if (query.Length == 0) return true;
        return KanaNormalizer.NormalizeKey(entry.Phrase).Contains(query, StringComparison.Ordinal) ||
               KanaNormalizer.NormalizeKey(entry.Expansion).Contains(query, StringComparison.Ordinal);
    }

    private void UpdateListState()
    {
        int total = _rows.Count;
        int shown = _rows.Count(MatchesSearch);
        CountText.Text = shown == total ? $"{total} 件" : $"{shown} / {total} 件";

        if (total == 0)
        {
            EmptyText.Text = "まだ登録がありません。上の欄に入力して［追加］を押してください。";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (shown == 0)
        {
            EmptyText.Text = "検索に一致する項目がありません。";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void DeleteRows_Click(object sender, RoutedEventArgs e) => DeleteSelectedRows();

    private void DeleteSelectedRows()
    {
        var targets = Grid.SelectedItems.OfType<SnippetEntry>().ToList();
        if (targets.Count == 0)
        {
            FormStatus.Text = "削除する項目を一覧から選んでください。";
            return;
        }

        if (targets.Count > 1)
        {
            var answer = MessageBox.Show(this, $"選択した {targets.Count} 件を削除します。この操作は取り消せません。",
                "定型文の削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
        }

        foreach (var t in targets) _rows.Remove(t);
        Persist();
        ClearForm();
        FormStatus.Text = targets.Count == 1
            ? $"「{targets[0].Phrase}」を削除しました。"
            : $"{targets.Count} 件を削除しました。";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "定型文を CSV に書き出す",
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = "voicedock-snippets.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _snippets.ExportCsv(dialog.FileName);
            ToastWindow.Show("定型文を CSV に書き出しました。住所などの個人情報が含まれる場合は取り扱いに注意してください。", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _log.Error($"定型文の書き出しに失敗しました: {ex}");
            ToastWindow.Show($"書き出せませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "CSV から定型文を読み込む",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var answer = MessageBox.Show(this,
            $"現在登録されている {_rows.Count} 件をすべて削除し、選択したファイルの内容に置き換えます。\n" +
            "この操作は取り消せません。続けますか？",
            "CSV から読み込む", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            int count = _snippets.ImportCsv(dialog.FileName);
            _rows.Clear();
            foreach (var entry in _snippets.Entries) _rows.Add(entry);
            ClearForm();
            ToastWindow.Show($"定型文を CSV から読み込みました（{count} 件）。");
        }
        catch (Exception ex)
        {
            _log.Error($"定型文の読み込みに失敗しました: {ex}");
            ToastWindow.Show($"読み込めませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    // ───────────── 保存・終了 ─────────────

    private void Persist() => _snippets.Replace(_rows);

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!HasUnsavedInput()) return;
        var answer = MessageBox.Show(this,
            "入力欄の内容はまだ登録されていません。登録せずに閉じますか？",
            "定型文", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }
}
