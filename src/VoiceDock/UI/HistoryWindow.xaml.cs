using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoiceDock.Services;

namespace VoiceDock.UI;

/// <summary>
/// 認識履歴の画面。直近の発話を一覧し、もう一度入力・コピー・辞書への登録ができる。
/// 履歴そのものは RecordingController がメモリにだけ持っており、この画面は表示するだけ。
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>一覧の 1 行（入力先が空のときに分かる表示を添える）。</summary>
    public sealed record Row(DateTime Time, string Text, string App)
    {
        public string AppLabel => App.Length > 0 ? App : "（入力されず）";
    }

    private readonly RecordingController _controller;
    private readonly Action<string> _registerToDictionary;
    private readonly ObservableCollection<Row> _rows = new();

    public HistoryWindow(RecordingController controller, Action<string> registerToDictionary)
    {
        InitializeComponent();
        AppTheme.ApplyToWindow(this);
        _controller = controller;
        _registerToDictionary = registerToDictionary;

        foreach (var r in controller.History) _rows.Add(ToRow(r));
        Grid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateState();

        _controller.HistoryAdded += OnHistoryAdded;
        _controller.HistoryCleared += OnHistoryCleared;

        UpdateState();
        if (_rows.Count > 0) Grid.SelectedIndex = 0;
    }

    private static Row ToRow(RecognitionRecord r) => new(r.Time, r.Text, r.App);

    private void OnHistoryAdded(RecognitionRecord record) =>
        Dispatcher.BeginInvoke(() =>
        {
            _rows.Insert(0, ToRow(record));
            UpdateTarget();
        });

    private void OnHistoryCleared() => Dispatcher.BeginInvoke(() => _rows.Clear());

    private Row? Selected => Grid.SelectedItem as Row;

    private void UpdateState()
    {
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool has = Selected != null;
        ReinjectButton.IsEnabled = has;
        CopyButton.IsEnabled = has;
        RegisterButton.IsEnabled = has;
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        var app = _controller.LastTargetApp;
        TargetText.Text = app.Length > 0
            ? $"「もう一度入力」の入力先: {app}（最後に音声入力したアプリ）"
            : "「もう一度入力」は、音声入力で一度文字を入力したあとに使えます。";
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 行そのものをダブルクリックしたときだけ入力する。見出しやスクロールバーの
        // ダブルクリックで、意図せず他のアプリへ文字が入らないようにする
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(Grid, source) is not DataGridRow) return;
        if (Selected != null) Reinject_Click(sender, e);
    }

    private async void Reinject_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (!await _controller.ReinjectAsync(row.Text))
        {
            ToastWindow.Show("入力先のアプリが見つかりません。［コピー］して貼り付けてください。", ToastKind.Warning);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try
        {
            Clipboard.SetDataObject(row.Text, true);
            ToastWindow.Show("コピーしました。");
        }
        catch (Exception ex)
        {
            ToastWindow.Show($"コピーできませんでした。{UserMessage.Describe(ex)}", ToastKind.Error);
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) _registerToDictionary(row.Text);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        var answer = MessageBox.Show(this, "認識履歴をすべて消去します。よろしいですか？",
            "認識履歴", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _controller.ClearHistory();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _controller.HistoryAdded -= OnHistoryAdded;
        _controller.HistoryCleared -= OnHistoryCleared;
    }
}
