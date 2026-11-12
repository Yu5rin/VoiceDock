using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// アプリ（プロセス名）ごとに、入力方式と「改行」の送り方を上書きする画面。変更は即時保存する。
/// </summary>
public partial class AppRulesWindow : Window
{
    /// <summary>入力方式の選択肢（先頭は「既定に従う」）。</summary>
    private static readonly InputMethod?[] MethodChoices = { null, InputMethod.SendInput, InputMethod.Clipboard };

    /// <summary>改行の送り方の選択肢（先頭は「既定に従う」）。</summary>
    private static readonly NewlineMode?[] NewlineChoices =
        { null, NewlineMode.ShiftEnter, NewlineMode.Enter, NewlineMode.AltEnter };

    /// <summary>1 行ぶんの表示用モデル。選択の変更を即座に保存へ反映する。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private int _methodIndex;
        private int _newlineIndex;

        public required string AppName { get; init; }

        /// <summary>0 = 既定に従う / 1 = 直接キー入力 / 2 = クリップボード貼り付け</summary>
        public int MethodIndex
        {
            get => _methodIndex;
            set => Set(ref _methodIndex, value, nameof(MethodIndex));
        }

        /// <summary>0 = 既定に従う / 1 = Shift+Enter / 2 = Enter / 3 = Alt+Enter</summary>
        public int NewlineIndex
        {
            get => _newlineIndex;
            set => Set(ref _newlineIndex, value, nameof(NewlineIndex));
        }

        /// <summary>選択が変更されたときに呼ばれる（保存用）。</summary>
        public Action? Changed { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref int field, int value, string name)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            Changed?.Invoke();
        }
    }

    private readonly SettingsService _settings;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _loading = true;

    public AppRulesWindow(SettingsService settings, IReadOnlyList<string> knownApps)
    {
        InitializeComponent();
        WindowBehavior.Attach(this, "AppRules", closeOnEscape: false);   // Esc は「閉じる」ボタン（IsCancel）が受ける
        _settings = settings;

        var s = settings.Current;
        var apps = s.AppInputMethods.Keys.Concat(s.AppNewlineModes.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a, StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            var row = CreateRow(app);
            row.MethodIndex = s.AppInputMethods.TryGetValue(app, out var m) ? Array.IndexOf(MethodChoices, m) : 0;
            row.NewlineIndex = s.AppNewlineModes.TryGetValue(app, out var n) ? Array.IndexOf(NewlineChoices, n) : 0;
            _rows.Add(row);
        }
        Grid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateEmptyState();

        // 入力先として使ったことのあるアプリを候補に出す（未登録のもののみ）
        foreach (var app in knownApps.Where(a => !_rows.Any(r => string.Equals(r.AppName, a, StringComparison.OrdinalIgnoreCase))))
            AppCombo.Items.Add(app);

        _loading = false;
        UpdateEmptyState();
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private Row CreateRow(string appName) => new() { AppName = appName, Changed = Persist };

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = (AppCombo.Text ?? "").Trim();
        if (name.Length == 0) return;

        // 「.exe」付きで入力された場合に備えて取り除く
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (_rows.Any(r => string.Equals(r.AppName, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"「{name}」は既に一覧にあります。";
            return;
        }

        // 何を変えたいかはアプリによって違うため、どちらも「既定に従う」で追加し、選んでもらう
        _rows.Add(CreateRow(name));
        AppCombo.Items.Remove(name);
        AppCombo.Text = "";
        StatusText.Text = $"「{name}」を追加しました。文字の入れ方か改行のキーを選んでください（両方「共通の設定に従う」のままなら、何も変わらないため保存しません）。";
        Grid.SelectedItem = _rows[^1];
        Grid.ScrollIntoView(_rows[^1]);
    }

    private void AppCombo_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Add_Click(sender, e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Grid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        DeleteButton.IsEnabled = Grid.SelectedItems.Count > 0;

    private void Grid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 表の標準の削除に任せると、画面からは消えても保存されず、開き直すと戻ってしまう
        if (e.Key != System.Windows.Input.Key.Delete || e.OriginalSource is System.Windows.Controls.TextBox) return;
        e.Handled = true;
        Delete_Click(sender, e);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var targets = Grid.SelectedItems.OfType<Row>().ToList();
        if (targets.Count == 0) return;
        foreach (var row in targets)
        {
            _rows.Remove(row);
            // 消したアプリは、また候補から選べるようにする
            if (!AppCombo.Items.Contains(row.AppName)) AppCombo.Items.Add(row.AppName);
        }
        Persist();
        StatusText.Text = targets.Count == 1
            ? $"「{targets[0].AppName}」の設定を削除しました（共通の設定で動きます）。"
            : $"{targets.Count} 件の設定を削除しました（共通の設定で動きます）。";
    }

    private void Persist()
    {
        if (_loading) return;
        var methods = new Dictionary<string, InputMethod>(StringComparer.OrdinalIgnoreCase);
        var newlines = new Dictionary<string, NewlineMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _rows)
        {
            if (MethodChoices.ElementAtOrDefault(r.MethodIndex) is { } m) methods[r.AppName] = m;
            if (NewlineChoices.ElementAtOrDefault(r.NewlineIndex) is { } n) newlines[r.AppName] = n;
        }
        _settings.Update(s =>
        {
            s.AppInputMethods = methods;
            s.AppNewlineModes = newlines;
        });
    }
}
