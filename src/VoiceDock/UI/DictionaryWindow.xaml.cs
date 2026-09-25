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
/// 辞書管理画面。上のフォームで 1 件ずつ登録・編集し、下の一覧で確認・検索・削除する。
///
/// 以前は表のセルを直接編集する作りだったが、新しい行の入れ方が分かりにくく、
/// 事実上 Excel から貼り付けるしか登録手段が無かった。アプリ内で完結して
/// 登録できるよう、入力フォームを中心にした画面にしている。
/// 変更は操作のたびに即時保存する。
/// </summary>
public partial class DictionaryWindow : Window
{
    private readonly DictionaryService _dictionary;
    private readonly LogService _log;
    private readonly ObservableCollection<DictionaryEntry> _rows;
    private readonly ICollectionView _view;

    /// <summary>フォームで編集中の項目。null なら新規登録の状態。</summary>
    private DictionaryEntry? _editing;

    /// <summary>フォームへ読み込んでいる最中は、選択変更などの連鎖を無視する。</summary>
    private bool _loadingForm;

    public DictionaryWindow(DictionaryService dictionary, LogService log)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _dictionary = dictionary;
        _log = log;

        _rows = new ObservableCollection<DictionaryEntry>(dictionary.Entries);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => o is DictionaryEntry e && MatchesSearch(e);
        Grid.ItemsSource = _view;
        _rows.CollectionChanged += (_, _) => UpdateListState();

        WrongBox.KeyDown += FormBox_KeyDown;
        CorrectBox.KeyDown += FormBox_KeyDown;
        Grid.PreviewKeyDown += Grid_PreviewKeyDown;
        PreviewKeyDown += Window_PreviewKeyDown;

        ClearForm();
        UpdateListState();
        Loaded += (_, _) => WrongBox.Focus();
    }

    // ───────────── フォーム ─────────────

    /// <summary>
    /// 読みの欄に文字を入れた状態でフォームを開く（「直前の入力を辞書に登録」から使う）。
    /// 誤認識された部分だけ残して、表記を入力してもらう想定。
    /// </summary>
    public void PrefillWrong(string text)
    {
        ClearForm();
        WrongBox.Text = text.Trim();
        WrongBox.Focus();
        WrongBox.SelectAll();
        FormStatus.Text = "誤認識された部分だけを残し、右に正しい表記を入力して［追加］してください。";
    }

    private void FormBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        // 読みの欄で Enter を押したら、まず表記の欄へ進む
        if (sender == WrongBox && CorrectBox.Text.Trim().Length == 0)
        {
            CorrectBox.Focus();
            return;
        }
        Submit();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Submit();

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Grid.UnselectAll();
        ClearForm();
        WrongBox.Focus();
    }

    /// <summary>フォームの内容で追加（新規）または更新（編集中）する。</summary>
    private void Submit()
    {
        var wrong = WrongBox.Text.Trim();
        var correct = CorrectBox.Text.Trim();

        if (wrong.Length == 0)
        {
            FormStatus.Text = "読み・誤認識語を入力してください。";
            WrongBox.Focus();
            return;
        }
        if (correct.Length == 0)
        {
            FormStatus.Text = "出したい表記を入力してください。";
            CorrectBox.Focus();
            return;
        }

        // 照合はひらがな・カタカナを区別しないため、重複もその基準で判定する
        var key = KanaNormalizer.NormalizeKey(wrong);
        var duplicate = _rows.FirstOrDefault(r => !ReferenceEquals(r, _editing) &&
                                                  KanaNormalizer.NormalizeKey(r.Wrong) == key);
        if (duplicate != null)
        {
            var answer = MessageBox.Show(this,
                $"「{duplicate.Wrong} → {duplicate.Correct}」が既に登録されています。\n" +
                "（ひらがな・カタカナの違いは同じ語として扱います）\n\n" +
                $"「{correct}」に置き換えますか？",
                "同じ読みの登録があります", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            _rows.Remove(duplicate);
        }

        var entry = new DictionaryEntry { Wrong = wrong, Correct = correct, WholeOnly = WholeOnlyCheck.IsChecked == true };
        // 編集・上書きのときは、それまでの使用回数を引き継ぐ
        var previous = new[] { _editing, duplicate }.Where(x => x != null).OrderByDescending(x => x!.UseCount).FirstOrDefault();
        if (previous != null)
        {
            entry.UseCount = previous.UseCount;
            entry.LastUsed = previous.LastUsed;
        }
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
            ? $"「{wrong} → {correct}」に更新しました。"
            : $"「{wrong} → {correct}」を登録しました。";
        WrongBox.Focus();
        RefreshTry();
    }

    private void ClearForm()
    {
        _loadingForm = true;
        _editing = null;
        WrongBox.Text = "";
        CorrectBox.Text = "";
        WholeOnlyCheck.IsChecked = false;
        PrimaryButton.Content = "追加";
        FormStatus.Text = "";
        _loadingForm = false;
    }

    private void LoadIntoForm(DictionaryEntry entry)
    {
        _loadingForm = true;
        _editing = entry;
        WrongBox.Text = entry.Wrong;
        CorrectBox.Text = entry.Correct;
        WholeOnlyCheck.IsChecked = entry.WholeOnly;
        PrimaryButton.Content = "更新";
        FormStatus.Text = "選択した項目を編集しています。［新規入力］で新しく登録する状態に戻ります。";
        _loadingForm = false;
    }

    /// <summary>フォームに、まだ登録していない入力が残っているか。</summary>
    private bool HasUnsavedInput()
    {
        var wrong = WrongBox.Text.Trim();
        var correct = CorrectBox.Text.Trim();
        if (_editing == null) return wrong.Length > 0 || correct.Length > 0;
        return wrong != _editing.Wrong || correct != _editing.Correct ||
               (WholeOnlyCheck.IsChecked == true) != _editing.WholeOnly;
    }

    // ───────────── 一覧 ─────────────

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingForm) return;
        // 1 件だけ選んだときに、その内容をフォームへ読み込んで編集できるようにする
        if (Grid.SelectedItems.Count == 1 && Grid.SelectedItem is DictionaryEntry entry)
            LoadIntoForm(entry);
        else if (Grid.SelectedItems.Count == 0 && _editing != null)
            ClearForm();
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            PasteFromClipboard();
        }
        else if (e.Key == Key.Delete)
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

    private bool MatchesSearch(DictionaryEntry entry)
    {
        var query = KanaNormalizer.NormalizeKey(SearchBox?.Text);
        if (query.Length == 0) return true;
        return KanaNormalizer.NormalizeKey(entry.Wrong).Contains(query, StringComparison.Ordinal) ||
               KanaNormalizer.NormalizeKey(entry.Correct).Contains(query, StringComparison.Ordinal);
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
        var targets = Grid.SelectedItems.OfType<DictionaryEntry>().ToList();
        if (targets.Count == 0)
        {
            FormStatus.Text = "削除する項目を一覧から選んでください。";
            return;
        }

        // 複数件を一度に消す場合は、取り消せないため確認する
        if (targets.Count > 1)
        {
            var answer = MessageBox.Show(this, $"選択した {targets.Count} 件を削除します。この操作は取り消せません。",
                "辞書の削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
        }

        foreach (var t in targets) _rows.Remove(t);
        Persist();
        ClearForm();
        FormStatus.Text = targets.Count == 1
            ? $"「{targets[0].Wrong} → {targets[0].Correct}」を削除しました。"
            : $"{targets.Count} 件を削除しました。";
        RefreshTry();
    }

    /// <summary>
    /// クリップボードのタブ区切りテキスト（Excel のセル範囲コピー形式）を取り込む。
    /// 同じ読みが既にあれば、表記を上書きする。
    /// </summary>
    private void PasteFromClipboard()
    {
        if (!Clipboard.ContainsText()) return;
        int added = 0, updated = 0;

        foreach (var lineRaw in Clipboard.GetText().Split('\n'))
        {
            var cells = lineRaw.TrimEnd('\r').Split('\t');
            var wrong = cells.ElementAtOrDefault(0)?.Trim() ?? "";
            var correct = cells.ElementAtOrDefault(1)?.Trim() ?? "";
            bool wholeOnly = DictionaryService.ParseWholeOnly(cells.ElementAtOrDefault(2));
            if (wrong.Length == 0 || correct.Length == 0) continue;

            var entry = new DictionaryEntry { Wrong = wrong, Correct = correct, WholeOnly = wholeOnly };
            var key = KanaNormalizer.NormalizeKey(wrong);
            var existing = _rows.FirstOrDefault(r => KanaNormalizer.NormalizeKey(r.Wrong) == key);
            if (existing != null)
            {
                entry.UseCount = existing.UseCount;
                entry.LastUsed = existing.LastUsed;
                _rows[_rows.IndexOf(existing)] = entry;
                updated++;
            }
            else
            {
                _rows.Add(entry);
                added++;
            }
        }

        if (added + updated == 0)
        {
            FormStatus.Text = "貼り付けられる内容がありませんでした（Excel の 2 列をコピーしてください）。";
            return;
        }
        Persist();
        ClearForm();
        FormStatus.Text = updated > 0 ? $"{added} 件を追加、{updated} 件を更新しました。" : $"{added} 件を追加しました。";
        RefreshTry();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "辞書を CSV に書き出す",
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = "voicedock-dictionary.csv",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _dictionary.ExportCsv(dialog.FileName);
            ToastWindow.Show("辞書を CSV に書き出しました。ファイルには社内用語・人名が含まれるため取り扱いに注意してください。", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _log.Error($"辞書の書き出しに失敗しました: {ex}");
            ToastWindow.Show($"書き出せませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "CSV から辞書を読み込む",
            Filter = "CSV ファイル (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        // 既存の内容をすべて置き換えるため、取り消しがきかない。実行前に確認する
        var answer = MessageBox.Show(this,
            $"現在登録されている {_rows.Count} 件をすべて削除し、選択したファイルの内容に置き換えます。\n" +
            "この操作は取り消せません。続けますか？",
            "CSV から読み込む", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            int count = _dictionary.ImportCsv(dialog.FileName);
            _rows.Clear();
            foreach (var entry in _dictionary.Entries) _rows.Add(entry);
            ClearForm();
            ToastWindow.Show($"辞書を CSV から読み込みました（{count} 件）。");
            RefreshTry();
        }
        catch (Exception ex)
        {
            _log.Error($"辞書の読み込みに失敗しました: {ex}");
            ToastWindow.Show($"読み込めませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    // ───────────── 試す ─────────────

    private void TryBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshTry();

    private void RefreshTry()
    {
        var input = TryBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            TryResult.Text = "";
            return;
        }
        var output = _dictionary.ApplyReplacements(input);
        TryResult.Text = output == input
            ? $"→ {output}（置き換えなし）"
            : $"→ {output}";
    }

    // ───────────── 保存・終了 ─────────────

    private void Persist() => _dictionary.Replace(_rows);

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (!HasUnsavedInput()) return;
        var answer = MessageBox.Show(this,
            "入力欄の内容はまだ登録されていません。登録せずに閉じますか？",
            "辞書管理", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }
}
