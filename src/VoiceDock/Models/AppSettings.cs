namespace VoiceDock.Models;

/// <summary>認識に使用するブラウザの選択方針。</summary>
public enum BrowserChoice
{
    /// <summary>Windows の既定ブラウザに合わせる（Chrome 系なら Chrome、それ以外は Edge）</summary>
    Auto,
    /// <summary>常に Microsoft Edge を使う</summary>
    Edge,
    /// <summary>常に Google Chrome を使う</summary>
    Chrome,
}

/// <summary>テキストの入力方式。</summary>
public enum InputMethod
{
    /// <summary>SendInput による直接キー入力（既定）</summary>
    SendInput,
    /// <summary>クリップボード経由の貼り付け (Ctrl+V)。直接入力を受け付けないアプリ向け</summary>
    Clipboard,
}

/// <summary>更新を確認するタイミング。</summary>
public enum UpdateCheckMode
{
    /// <summary>起動時に確認する。ただし前回の確認から 24 時間以上経っている場合のみ</summary>
    DailyOnStartup,
    /// <summary>起動のたびに毎回確認する（既定）</summary>
    EveryStartup,
    /// <summary>自動では確認せず、手動で「更新を確認」したときだけ</summary>
    Manual,
}

/// <summary>改行の送出方法。</summary>
public enum NewlineMode
{
    /// <summary>
    /// Shift+Enter で改行する（既定）。メモ帳等の通常の入力欄でも改行として扱われ、
    /// かつ Enter が「送信」になるチャットアプリでも誤送信しないため、最も安全。
    /// </summary>
    ShiftEnter,
    /// <summary>Enter キーで改行する。Shift+Enter が効かない一部のアプリ向け</summary>
    Enter,
    /// <summary>Alt+Enter で改行する。Excel のセル内改行向け</summary>
    AltEnter,
}

/// <summary>認識結果の英数字の幅のそろえ方。</summary>
public enum CharacterWidth
{
    /// <summary>認識結果のまま（既定）</summary>
    AsIs,
    /// <summary>半角にそろえる（例: ３時 → 3時）</summary>
    Half,
    /// <summary>全角にそろえる（例: 3時 → ３時）</summary>
    Full,
}

/// <summary>ホットキーの操作方式。</summary>
public enum HotkeyMode
{
    /// <summary>押すたびに録音開始/停止を切り替える（既定）</summary>
    Toggle,
    /// <summary>キーを押している間だけ録音する</summary>
    PushToTalk,
}

/// <summary>
/// アプリ設定。%APPDATA%\VoiceDock\settings.json に保存される。
/// </summary>
public class AppSettings
{
    /// <summary>録音開始/停止のトグルホットキー（例: "Ctrl+Space"）</summary>
    public string Hotkey { get; set; } = "Ctrl+Space";

    /// <summary>ホットキーの操作方式（トグル / 押している間だけ録音）</summary>
    public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Toggle;

    /// <summary>直前に入力したテキストを取り消すホットキー</summary>
    public string UndoHotkey { get; set; } = "Ctrl+Shift+Space";

    /// <summary>直前の入力の取り消し（音声コマンド「とりけし」およびホットキー）を有効にする</summary>
    public bool UndoEnabled { get; set; } = true;

    /// <summary>
    /// 同じ文章を何度も取り消したら、辞書への登録を勧める。
    /// 取り消した文章はメモリにだけ持ち、ファイルには保存しない（認識履歴と同じ扱い）。
    /// </summary>
    public bool SuggestFrequentUndo { get; set; } = true;

    /// <summary>Windows 起動時の自動起動</summary>
    public bool StartupEnabled { get; set; } = true;

    /// <summary>テキストの入力方式</summary>
    public InputMethod InputMethod { get; set; } = InputMethod.SendInput;

    /// <summary>
    /// 「改行」コマンドの送出方法。
    /// Enter が送信になるチャットアプリ（Claude Desktop, Slack, Teams 等）が主用途のため、
    /// 既定は Shift+Enter とする。
    /// </summary>
    public NewlineMode NewlineMode { get; set; } = NewlineMode.ShiftEnter;

    /// <summary>
    /// アプリ（プロセス名）ごとの入力方式の上書き。
    /// ここに登録されたアプリが前面にある場合は、既定の入力方式より優先される。
    /// キーは拡張子なしのプロセス名（大文字小文字は区別しない）。
    /// </summary>
    public Dictionary<string, InputMethod> AppInputMethods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// アプリ（プロセス名）ごとの改行の送り方の上書き。
    /// チャットは Shift+Enter、Excel は Alt+Enter のように、アプリによって改行のキーが違うため。
    /// ここに無いアプリは <see cref="NewlineMode"/> に従う。キーは拡張子なしのプロセス名。
    /// </summary>
    public Dictionary<string, NewlineMode> AppNewlineModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 音声認識の言語（Web Speech API の言語コード）。既定は日本語。
    /// 選べる値は <see cref="RecognitionLanguages"/> を参照。
    /// </summary>
    public string RecognitionLanguage { get; set; } = RecognitionLanguages.Japanese;

    /// <summary>認識に使用するブラウザ（既定は Windows の既定ブラウザに追従）</summary>
    public BrowserChoice Browser { get; set; } = BrowserChoice.Auto;

    /// <summary>
    /// 対応環境では端末内で音声認識を行う（音声をクラウドへ送信しない）。
    /// 非対応のブラウザ・言語パック未導入の場合は自動でクラウド認識にフォールバックする。
    /// </summary>
    public bool PreferLocalRecognition { get; set; }

    /// <summary>
    /// 音声コマンドを有効にする。改行・スペース等の操作、句読点（「まる」「てん」）、
    /// 記号（「アットマーク」等）の発話入力をまとめて制御する。
    /// </summary>
    public bool VoiceCommandsEnabled { get; set; } = true;

    /// <summary>
    /// キー操作の音声コマンド（「送信」→ Enter、「全選択」→ Ctrl+A など）を有効にする。
    /// 発話全体がコマンド語と一致したときだけ働く。
    /// </summary>
    public bool KeyCommandsEnabled { get; set; } = true;

    /// <summary>定型文スニペット（「じゅうしょ」→ 住所全文 等）を有効にする</summary>
    public bool SnippetsEnabled { get; set; } = true;

    /// <summary>認識結果から日本語間の不要な半角スペースを除去する</summary>
    public bool RemoveSpaces { get; set; } = true;

    /// <summary>認識結果の英数字を半角・全角にそろえる（日本語で認識しているときのみ）</summary>
    public CharacterWidth CharacterWidth { get; set; } = CharacterWidth.AsIs;

    /// <summary>「えーと」「あのー」などのつなぎ言葉を取り除く</summary>
    public bool RemoveFillers { get; set; }

    /// <summary>
    /// 無音がこの秒数続いたら音声入力を自動停止する。0 なら自動停止しない。
    /// 選べる値は <see cref="SilenceAutoStopChoices"/>。
    /// </summary>
    public int SilenceAutoStopSeconds { get; set; } = 30;

    /// <summary>無音自動停止の秒数として選べる値（0 は自動停止しない）。</summary>
    public static readonly int[] SilenceAutoStopChoices = { 15, 30, 60, 0 };

    /// <summary>発話の区切りごとに文末へ「。」を自動挿入する</summary>
    public bool AutoPeriod { get; set; } = false;

    /// <summary>録音開始/停止時に操作音を鳴らす</summary>
    public bool SoundFeedback { get; set; } = true;

    /// <summary>
    /// 認識したテキストをログファイルに保存する。
    ///
    /// 音声入力の内容そのもの（メールの下書き、顧客名、読み上げたパスワード等）が
    /// 平文で 30 日間残るため、既定では保存しない。動作確認や誤認識の調査が必要な
    /// ときだけ有効にする想定。
    /// </summary>
    public bool LogRecognitionText { get; set; }

    /// <summary>初回起動ガイドを表示済みかどうか</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>更新を確認するタイミング</summary>
    public UpdateCheckMode UpdateCheckMode { get; set; } = UpdateCheckMode.EveryStartup;

    /// <summary>
    /// 更新の確認先（GitHub Releases API）。
    /// どこへ通信するのかが利用者から見えるよう、また配布先を移した際に設定変更だけで
    /// 済むよう、コードに直書きせず設定ファイルに持たせている。
    /// </summary>
    public string UpdateApiUrl { get; set; } = "https://api.github.com/repos/Yu5rin/VoiceDock/releases/latest";

    /// <summary>この版は案内しない、と利用者が指定したバージョン（例: "0.7.2"）。</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>前回、更新を確認した日時 (UTC)。未確認なら null</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        // 参照型は複製しないと、コピー元と同じ辞書を共有してしまう
        clone.AppInputMethods = new Dictionary<string, InputMethod>(AppInputMethods, StringComparer.OrdinalIgnoreCase);
        clone.AppNewlineModes = new Dictionary<string, NewlineMode>(AppNewlineModes, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}
