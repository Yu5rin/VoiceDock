using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using VoiceDock.Models;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// アプリ（プロセス名）ごとの入力方式を管理する画面。変更は即時保存する。
/// </summary>
public partial class AppRulesWindow : Window
{
    /// <summary>1 行ぶんの表示用モデル。入力方式の変更を即座に保存へ反映する。</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private int _methodIndex;

        public required string AppName { get; init; }

        /// <summary>0 = 直接キー入力 / 1 = クリップボード貼り付け</summary>
        public int MethodIndex
        {
            get => _methodIndex;
            set
            {
                if (_methodIndex == value) return;
                _methodIndex = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodIndex)));
                Changed?.Invoke();
            }
        }

        /// <summary>入力方式が変更されたときに呼ばれる（保存用）。</summary>
        public Action? Changed { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly SettingsService _settings;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _loading = true;

    public AppRulesWindow(SettingsService settings, IReadOnlyList<string> knownApps)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _settings = settings;

        foreach (var kv in settings.Current.AppInputMethods.OrderBy(k => k.Key))
            _rows.Add(CreateRow(kv.Key, kv.Value));
        Grid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateEmptyState();

        // 入力先として使ったことのあるアプリを候補に出す（未登録のもののみ）
        foreach (var app in knownApps.Where(a => !settings.Current.AppInputMethods.ContainsKey(a)))
            AppCombo.Items.Add(app);

        _loading = false;
        UpdateEmptyState();
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private Row CreateRow(string appName, InputMethod method) => new()
    {
        AppName = appName,
        MethodIndex = method == InputMethod.Clipboard ? 1 : 0,
        Changed = Persist,
    };

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = (AppCombo.Text ?? "").Trim();
        if (name.Length == 0) return;

        // 「.exe」付きで入力された場合に備えて取り除く
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (_rows.Any(r => string.Equals(r.AppName, name, StringComparison.OrdinalIgnoreCase)))
        {
            ToastWindow.Show($"「{name}」は既に登録されています。", ToastKind.Warning);
            return;
        }

        // 直接入力で困っているケースが大半のため、既定はクリップボード貼り付けにする
        _rows.Add(CreateRow(name, InputMethod.Clipboard));
        AppCombo.Items.Remove(name);
        AppCombo.Text = "";
        Persist();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Grid.SelectedItems.OfType<Row>().ToList())
            _rows.Remove(row);
        Persist();
    }

    private void Persist()
    {
        if (_loading) return;
        var map = _rows.ToDictionary(
            r => r.AppName,
            r => r.MethodIndex == 1 ? InputMethod.Clipboard : InputMethod.SendInput,
            StringComparer.OrdinalIgnoreCase);
        _settings.Update(s => s.AppInputMethods = map);
    }
}
