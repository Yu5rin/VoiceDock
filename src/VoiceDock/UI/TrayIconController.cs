using System.Drawing;
using System.Windows.Forms;
using VoiceDock.Models;

namespace VoiceDock.UI;

/// <summary>
/// タスクトレイ常駐アイコン。状態に応じて色を変え、右クリックメニューを提供する。
///
/// メニューは「よく使う順」に並べる。
/// ・いちばん上: 音声入力の開始/停止、直前の入力の取り消し・辞書登録、認識言語
/// ・次: 中身を育てる画面（辞書・定型文・認識履歴）
/// ・次: 設定
/// ・下: たまにしか使わないもの（トラブル解決・ヘルプ）はサブメニューにまとめる
/// 画面が開く項目には「…」を付け、その場で実行される項目と見分けられるようにする。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _recordItem;
    private readonly ToolStripMenuItem _undoItem;
    private readonly ToolStripMenuItem _registerItem;
    private readonly ToolStripMenuItem _updateAvailableItem;
    private readonly ToolStripSeparator _updateAvailableSeparator;
    private readonly ToolStripMenuItem _languageItem;
    private readonly Font _menuFont = new("Yu Gothic UI", 9f);
    private readonly Font _menuFontBold = new("Yu Gothic UI", 9f, FontStyle.Bold);
    private bool _listening;
    private TrayState _state = TrayState.Idle;

    public event Action? RecordToggleRequested;
    public event Action? UndoRequested;
    public event Action? RegisterLastToDictionaryRequested;
    public event Action? HistoryRequested;
    public event Action<string>? LanguageSelected;
    public event Action? SettingsRequested;
    public event Action? AppRulesRequested;
    public event Action? DictionaryRequested;
    public event Action? SnippetRequested;
    public event Action? LogRequested;
    public event Action? RestartEngineRequested;
    public event Action? DiagnosticsRequested;
    public event Action? OpenDataFolderRequested;
    public event Action? HelpRequested;
    public event Action? AboutRequested;
    public event Action? UpdateCheckRequested;
    public event Action? ExitRequested;

    /// <summary>メニューを開くときに、いまの状態を問い合わせる口（開始できるか・取り消せるか・履歴があるか）。</summary>
    public Func<bool>? CanStart { get; set; }
    public Func<bool>? CanUndo { get; set; }
    public Func<bool>? HasHistory { get; set; }

    public TrayIconController()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = _menuFont,
        };

        // 新しい版が見つかっているときだけ、いちばん上に出す
        _updateAvailableItem = CreateItem("新しい版があります。更新する…", () => UpdateCheckRequested?.Invoke());
        _updateAvailableItem.Font = _menuFontBold;
        _updateAvailableItem.Visible = false;
        _updateAvailableSeparator = new ToolStripSeparator { Visible = false };
        menu.Items.Add(_updateAvailableItem);
        menu.Items.Add(_updateAvailableSeparator);

        _recordItem = CreateItem("音声入力を開始", () => RecordToggleRequested?.Invoke());
        _recordItem.Font = _menuFontBold;
        menu.Items.Add(_recordItem);
        _undoItem = CreateItem("直前の入力を取り消す", () => UndoRequested?.Invoke());
        menu.Items.Add(_undoItem);
        // 誤認識に気づいたその場で辞書に登録できるようにする（辞書は育ててこそ効く）
        _registerItem = CreateItem("直前の入力を辞書に登録…", () => RegisterLastToDictionaryRequested?.Invoke());
        menu.Items.Add(_registerItem);

        // 認識言語。英文メールなどを書くときにすぐ切り替えられるよう、トレイから選べるようにする
        _languageItem = new ToolStripMenuItem("認識言語");
        foreach (var (code, label) in RecognitionLanguages.All)
        {
            var item = new ToolStripMenuItem(label) { Tag = code };
            item.Click += (_, _) => LanguageSelected?.Invoke(code);
            _languageItem.DropDownItems.Add(item);
        }
        ApplyDarkTheme(_languageItem);
        menu.Items.Add(_languageItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("辞書…", () => DictionaryRequested?.Invoke()));
        menu.Items.Add(CreateItem("定型文…", () => SnippetRequested?.Invoke()));
        menu.Items.Add(CreateItem("認識履歴…", () => HistoryRequested?.Invoke()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("設定…", () => SettingsRequested?.Invoke()));
        menu.Items.Add(CreateItem("アプリ別の設定…", () => AppRulesRequested?.Invoke()));

        menu.Items.Add(new ToolStripSeparator());
        // 文字が入らない・認識しないときに使うものをまとめる
        var trouble = new ToolStripMenuItem("トラブル解決");
        trouble.DropDownItems.Add(CreateItem("動作チェック…", () => DiagnosticsRequested?.Invoke()));
        trouble.DropDownItems.Add(CreateItem("ログ…", () => LogRequested?.Invoke()));
        // ブラウザが落ちたまま戻らないときに、アプリを再起動せず立て直せるようにする
        trouble.DropDownItems.Add(CreateItem("認識エンジンを再起動", () => RestartEngineRequested?.Invoke()));
        trouble.DropDownItems.Add(new ToolStripSeparator());
        trouble.DropDownItems.Add(CreateItem("データの保存先を開く", () => OpenDataFolderRequested?.Invoke()));
        ApplyDarkTheme(trouble);
        menu.Items.Add(trouble);

        var help = new ToolStripMenuItem("ヘルプ");
        help.DropDownItems.Add(CreateItem("使い方（ブラウザで開く）", () => HelpRequested?.Invoke()));
        help.DropDownItems.Add(CreateItem("更新を確認…", () => UpdateCheckRequested?.Invoke()));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(CreateItem("VoiceDock について…", () => AboutRequested?.Invoke()));
        ApplyDarkTheme(help);
        menu.Items.Add(help);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("VoiceDock を終了", () => ExitRequested?.Invoke()));

        // 開くたびに、いま使える項目だけを押せるようにする
        menu.Opening += (_, _) => RefreshAvailability();

        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.Get(TrayState.Idle),
            Text = "VoiceDock - 準備中…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();

        // Windows の配色（タスクバーの明暗）を変えたら、アイコンを描き直す
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        try { _notifyIcon.Icon = IconFactory.Get(_state); } catch { /* 描き直せなくても動作に影響しない */ }
    }

    private static ToolStripMenuItem CreateItem(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>サブメニューは親の描画設定を引き継がないため、ダーク表示を明示する。</summary>
    private void ApplyDarkTheme(ToolStripMenuItem parent)
    {
        parent.DropDown.Renderer = new DarkMenuRenderer();
        parent.DropDown.Font = _menuFont;
        if (parent.DropDown is ToolStripDropDownMenu dropDown) dropDown.ShowImageMargin = false;
    }

    private void RefreshAvailability()
    {
        try
        {
            // 止めるのはいつでもできる。始めるのは認識エンジンの準備ができてから
            _recordItem.Enabled = _listening || (CanStart?.Invoke() ?? true);
            _undoItem.Enabled = CanUndo?.Invoke() ?? true;
            _registerItem.Enabled = HasHistory?.Invoke() ?? true;
        }
        catch
        {
            // 状態を読めなくてもメニューは開く
        }
    }

    /// <summary>状態に応じてメニューの「音声入力を開始/停止」表記を切り替える。</summary>
    public void SetListening(bool listening)
    {
        _listening = listening;
        _recordItem.Text = listening ? "音声入力を停止" : "音声入力を開始";
    }

    /// <summary>ホットキーをメニューの右側に表示する（覚えていなくても確かめられるように）。</summary>
    public void SetHotkeys(string record, string? undo)
    {
        _recordItem.ShortcutKeyDisplayString = record;
        _undoItem.ShortcutKeyDisplayString = undo ?? "";
    }

    /// <summary>
    /// 選択中の認識言語をメニューに反映する。
    /// チェックマークは暗い背景で見えにくいため、項目名の先頭に印を付けて示す。
    /// </summary>
    public void SetLanguage(string code)
    {
        _languageItem.Text = $"認識言語: {RecognitionLanguages.LabelOf(code)}";
        foreach (ToolStripMenuItem item in _languageItem.DropDownItems)
        {
            var label = RecognitionLanguages.LabelOf((string)item.Tag!);
            item.Text = (string)item.Tag! == code ? $"✓  {label}" : $"     {label}";
        }
    }

    /// <summary>更新が見つかっていることをメニュー上で示す。</summary>
    public void SetUpdateAvailable(bool available, string? version = null)
    {
        _updateAvailableItem.Text = version != null
            ? $"新しい版 v{version} があります。更新する…"
            : "新しい版があります。更新する…";
        _updateAvailableItem.Visible = available;
        _updateAvailableSeparator.Visible = available;
    }

    public void SetState(TrayState state, string? tooltip = null)
    {
        _state = state;
        _notifyIcon.Icon = IconFactory.Get(state);
        var text = tooltip ?? state switch
        {
            TrayState.Recording => "VoiceDock - 音声入力中",
            TrayState.Error => "VoiceDock - エラー（右クリック → トラブル解決）",
            _ => "VoiceDock - 待機中",
        };
        // NotifyIcon.Text は 63 文字制限
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menuFont.Dispose();
        _menuFontBold.Dispose();
    }

    /// <summary>ダークテーマに合わせた右クリックメニューの描画。</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Fg = Color.FromArgb(232, 232, 234);
        private static readonly Color DisabledFg = Color.FromArgb(124, 124, 134);

        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Fg : DisabledFg;
            base.OnRenderItemText(e);
        }

        /// <summary>サブメニューの矢印。標準の色は黒で、暗い背景では見えない。</summary>
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: false } ? DisabledFg : Fg;
            base.OnRenderArrow(e);
        }

        /// <summary>押せない項目は、マウスを載せても選択中の色にしない。</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Enabled) return;
            base.OnRenderMenuItemBackground(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(35, 35, 40);
        private static readonly Color Hover = Color.FromArgb(55, 55, 62);
        private static readonly Color Border = Color.FromArgb(60, 60, 68);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
