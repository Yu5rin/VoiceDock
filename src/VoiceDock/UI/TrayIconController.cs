using System.Drawing;
using System.Windows.Forms;

namespace VoiceDock.UI;

/// <summary>
/// タスクトレイ常駐アイコン。状態に応じて色を変え、
/// 右クリックメニュー（設定 / 辞書管理 / ログ表示 / 終了）を提供する。
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _recordItem;

    public event Action? RecordToggleRequested;
    public event Action? SettingsRequested;
    public event Action? DictionaryRequested;
    public event Action? LogRequested;
    public event Action? ExitRequested;

    public TrayIconController()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
        };
        _recordItem = CreateItem("録音開始", () => RecordToggleRequested?.Invoke());
        menu.Items.Add(_recordItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("設定", () => SettingsRequested?.Invoke()));
        menu.Items.Add(CreateItem("辞書管理", () => DictionaryRequested?.Invoke()));
        menu.Items.Add(CreateItem("ログ表示", () => LogRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("終了", () => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.Get(TrayState.Idle),
            Text = "VoiceDock - 待機中",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    private static ToolStripMenuItem CreateItem(string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>録音状態に応じてメニューの「録音開始/停止」表記を切り替える。</summary>
    public void SetListening(bool listening)
    {
        _recordItem.Text = listening ? "録音停止" : "録音開始";
    }

    public void SetState(TrayState state, string? tooltip = null)
    {
        _notifyIcon.Icon = IconFactory.Get(state);
        var text = tooltip ?? state switch
        {
            TrayState.Recording => "VoiceDock - 録音中",
            TrayState.Error => "VoiceDock - エラー",
            _ => "VoiceDock - 待機中",
        };
        // NotifyIcon.Text は 63 文字制限
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    /// <summary>ダークテーマに合わせた右クリックメニューの描画。</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.FromArgb(232, 232, 234);
            base.OnRenderItemText(e);
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
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
