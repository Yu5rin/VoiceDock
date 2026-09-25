using System.Windows;
using System.Windows.Threading;
using VoiceDock.Models;
using VoiceDock.UI;

namespace VoiceDock.Services;

/// <summary>認識履歴の 1 件。</summary>
/// <param name="Time">認識した時刻</param>
/// <param name="Text">入力した文字（辞書の置き換え後）</param>
/// <param name="App">入力先のアプリ（プロセス名）。入力欄が無く入らなかった場合は空</param>
public sealed record RecognitionRecord(DateTime Time, string Text, string App);

/// <summary>
/// 認識の開始/停止と、認識結果の入力欄への流し込みを統括する。
/// 実際の録音・認識はブラウザ(Web Speech API)が行い、本体は
/// 確定テキストを受け取って後処理（音声コマンド・辞書置換・整形）のうえ前面アプリへ入力する。
/// ホットキーのトグル、無音が続いたときの自動停止、トレイ・オーバーレイ制御を担う。
/// </summary>
public sealed class RecordingController : IDisposable
{

    /// <summary>
    /// 停止したあと、認識エンジンから最後の確定テキストが届くまでの猶予。
    /// 話し終えてすぐキーを離すと確定がわずかに遅れて届くため、
    /// ここで打ち切ると最後のひと言が丸ごと消えてしまう。
    /// </summary>
    private static readonly TimeSpan FinalGraceAfterStop = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 音声入力を止めてから（または最後に入力してから）IME を元に戻すまでの間。
    /// 送った文字を相手のアプリが読み終える前に IME を戻すと、残りの文字が IME を通って
    /// 欠けたり入れ替わったりするため、十分に待つ。停止直後に遅れて届く確定テキストも
    /// たいていこの間に入力し終わる。
    /// </summary>
    private static readonly TimeSpan ImeRestoreDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>同じ認識エラーを繰り返し通知しない間隔。</summary>
    private static readonly TimeSpan ErrorNoticeInterval = TimeSpan.FromMinutes(1);

    private readonly LogService _log;
    private readonly SettingsService _settings;
    private readonly TextProcessor _processor;
    private readonly SpeechBridgeServer _bridge;
    private readonly TrayIconController _tray;
    private readonly OverlayWindow _overlay;
    private readonly Dispatcher _dispatcher;

    private readonly object _sync = new();
    private bool _listening;
    private DispatcherTimer? _silenceTimer;

    /// <summary>音声入力が落ち着いたら IME を元に戻すためのタイマー。</summary>
    private readonly DispatcherTimer _imeRestoreTimer;
    private bool _localModeNotified;
    private bool _localFallbackNotified;

    /// <summary>停止した時刻。停止直後に届く確定テキストを取りこぼさないために見る。</summary>
    private DateTime _stoppedAtUtc = DateTime.MinValue;

    /// <summary>認識エラーの種類ごとの最終通知時刻。同じ内容の連発を抑える。</summary>
    private readonly Dictionary<string, DateTime> _errorNoticedAtUtc = new(StringComparer.Ordinal);

    /// <summary>エラーでトレイを赤くしているかどうか（復帰したら戻す）。</summary>
    private bool _inErrorState;

    /// <summary>
    /// 直前に入力したテキストの「見た目の文字数」（取り消し用）。0 なら取り消す対象なし。
    /// 絵文字などのサロゲートペアは 2 つの char で 1 文字のため、char 数ではなく
    /// 書記素（text element）数で数える。そうしないと BackSpace を余分に送ってしまう。
    /// </summary>
    private int _lastInjectedLength;

    /// <summary>直前に入力した先のアプリ（プロセス名）。別アプリでの誤爆を防ぐために使う。</summary>
    private string _lastInjectedApp = "";

    /// <summary>直前に入力した文章（音声コマンドで入れた記号などは空）。取り消した文章の集計に使う。</summary>
    private string _lastInjectedText = "";

    /// <summary>取り消した文章の集計。メモリにだけ持つ。</summary>
    private readonly UndoneTracker _undone = new();

    /// <summary>認識履歴として保持する最大件数。</summary>
    private const int MaxHistory = 200;

    /// <summary>
    /// 認識履歴（新しいものが先頭）。メモリにだけ持ち、ファイルには保存しない。
    /// 認識したテキストをファイルに残すかどうかの設定とは独立して、アプリの終了で消える。
    /// </summary>
    private readonly LinkedList<RecognitionRecord> _history = new();

    /// <summary>いま認識に使っている言語（認識中に設定が変わったかを判定するため）。</summary>
    private string _activeLanguage = RecognitionLanguages.Japanese;

    /// <summary>
    /// 英語など単語をスペースで区切る言語で、次の入力の前にスペースが要るかどうか。
    /// 区切りごとの認識結果をそのままつなぐと "hello" + "how are you" が
    /// "hellohow are you" になってしまうため、続きの入力にはスペースを補う。
    /// </summary>
    private bool _spaceBeforeNext;

    /// <summary><see cref="_spaceBeforeNext"/> が有効な入力先。別の入力先に移ったらリセットする。</summary>
    private IntPtr _spaceWindow;

    /// <summary>最後に文字を入力したウィンドウ。履歴から「もう一度入力」するときの入力先。</summary>
    private IntPtr _lastTargetWindow;

    /// <summary>最後に文字を入力したアプリの名前（画面表示用）。</summary>
    public string LastTargetApp { get; private set; } = "";

    /// <summary>認識履歴（新しいものが先頭）。</summary>
    public IReadOnlyList<RecognitionRecord> History
    {
        get { lock (_sync) return _history.ToList(); }
    }

    /// <summary>認識履歴に 1 件追加されたときに発火。</summary>
    public event Action<RecognitionRecord>? HistoryAdded;

    /// <summary>取り消した文章の集計が変わった（UI スレッドで通知）。</summary>
    public event Action? UndoneChanged;

    /// <summary>同じ文章を何度も取り消したので、辞書への登録を勧めたい（UI スレッドで通知）。</summary>
    public event Action<UndoneText>? FrequentUndoDetected;

    /// <summary>取り消した文章の集計（回数の多い順、同じ回数なら新しい順）。</summary>
    public IReadOnlyList<UndoneText> UndoneTexts => _undone.Snapshot();

    /// <summary>認識履歴を消去したときに発火。</summary>
    public event Action? HistoryCleared;

    /// <summary>入力先として観測したアプリ（プロセス名）。アプリ別設定の候補に使う。</summary>
    private readonly SortedSet<string> _seenApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>これまでに入力先となったアプリのプロセス名一覧。</summary>
    public IReadOnlyList<string> SeenApps
    {
        get { lock (_sync) return _seenApps.ToList(); }
    }

    /// <summary>録音状態の変化（トレイメニューの表記更新用）。</summary>
    public event Action<bool>? ListeningChanged;

    public bool IsListening
    {
        get { lock (_sync) return _listening; }
    }

    public RecordingController(LogService log, SettingsService settings, TextProcessor processor,
        SpeechBridgeServer bridge, TrayIconController tray, OverlayWindow overlay)
    {
        _log = log;
        _settings = settings;
        _processor = processor;
        _bridge = bridge;
        _tray = tray;
        _overlay = overlay;
        _dispatcher = Application.Current.Dispatcher;

        _imeRestoreTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = ImeRestoreDelay };
        _imeRestoreTimer.Tick += (_, _) =>
        {
            _imeRestoreTimer.Stop();
            // 音声入力を再開していたら、止めるまで IME はオフのままにする
            lock (_sync) if (_listening) return;
            TextInjector.RestoreSuppressedIme();
        };

        _bridge.FinalText += OnFinalText;
        _bridge.PartialText += _overlay.SetPartialText;
        _bridge.LevelChanged += _overlay.UpdateLevel;
        _bridge.RecognitionError += OnRecognitionError;
        _bridge.RecognitionModeReported += OnRecognitionModeReported;
        _settings.Changed += s => _dispatcher.BeginInvoke(() => OnSettingsChanged(s));
    }

    /// <summary>認識中に言語が変わったら、その場で新しい言語に切り替える。</summary>
    private void OnSettingsChanged(AppSettings s)
    {
        lock (_sync)
        {
            if (!_listening || s.RecognitionLanguage == _activeLanguage) return;
            _activeLanguage = s.RecognitionLanguage;
            _ = _bridge.StartRecognitionAsync(s.PreferLocalRecognition, _activeLanguage);
        }
        _log.Info($"認識言語を切り替えました: {RecognitionLanguages.LabelOf(s.RecognitionLanguage)}");
    }

    /// <summary>ホットキー押下時の入口。UI スレッドで呼ぶこと。</summary>
    public void Toggle()
    {
        // IME 変換中（未確定文字列がある状態）のホットキーは無視する
        if (ImeGuard.IsImeComposing())
        {
            _log.Info("IME 変換中のためホットキーを無視しました");
            return;
        }

        lock (_sync)
        {
            if (_listening) StopListening("ホットキー");
            else StartListening();
        }
    }

    /// <summary>トレイメニュー等からの開始/停止。UI スレッドで呼ぶこと。</summary>
    public void ToggleFromMenu()
    {
        lock (_sync)
        {
            if (_listening) StopListening("メニュー");
            else StartListening();
        }
    }

    /// <summary>
    /// 押している間だけ音声入力する方式で、ホットキーが押されたときの入口。
    /// IME 変換中は無視する。
    /// </summary>
    public void BeginPushToTalk()
    {
        if (ImeGuard.IsImeComposing())
        {
            _log.Info("IME 変換中のためホットキーを無視しました");
            return;
        }

        lock (_sync)
        {
            if (!_listening) StartListening();
        }
    }

    /// <summary>押しっぱなし方式で、ホットキーが離されたときの入口。</summary>
    public void EndPushToTalk()
    {
        lock (_sync)
        {
            if (_listening) StopListening("キーを離した");
        }
    }

    private void StartListening()
    {
        if (!_bridge.IsConnected)
        {
            _log.Warn("認識ブラウザが未接続のため音声入力を開始できません");
            ToastWindow.Show("認識エンジンの準備中です。数秒待ってからお試しください。", ToastKind.Warning);
            return;
        }

        _listening = true;
        _stoppedAtUtc = DateTime.MinValue;
        _imeRestoreTimer.Stop();
        _activeLanguage = _settings.Current.RecognitionLanguage;
        _ = _bridge.StartRecognitionAsync(_settings.Current.PreferLocalRecognition, _activeLanguage);
        _tray.SetState(TrayState.Recording);
        _overlay.ShowOverlay();
        ResetSilenceTimer();
        if (_settings.Current.SoundFeedback) SoundFeedback.PlayStart();
        _log.Info("音声入力を開始しました");
        ListeningChanged?.Invoke(true);
    }

    private void StopListening(string reason)
    {
        if (!_listening) return;
        _listening = false;
        _stoppedAtUtc = DateTime.UtcNow;
        ScheduleImeRestore();
        _ = _bridge.StopRecognitionAsync();
        StopSilenceTimer();
        _overlay.HideOverlay();
        _tray.SetState(TrayState.Idle);
        if (_settings.Current.SoundFeedback) SoundFeedback.PlayStop();
        _log.Info($"音声入力を停止しました ({reason})");
        ListeningChanged?.Invoke(false);
    }

    private void OnFinalText(string raw)
    {
        // 認識確定から入力までの体感遅延を減らすため、通常優先度のキュー待ちを
        // 挟まず最優先(Send)で処理する。
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            lock (_sync)
            {
                if (_listening)
                {
                    ResetSilenceTimer();
                }
                else if (DateTime.UtcNow - _stoppedAtUtc > FinalGraceAfterStop)
                {
                    // 停止からだいぶ経ってから届いたものは、別の場面の取りこぼしとみなす
                    return;
                }
            }

            // 認識できているのだから、直前のエラー表示は解除してよい
            ClearErrorState();

            _overlay.ClearPartialText();

            var processed = _processor.Process(raw);

            // 「取り消し」コマンド: 直前に入力した文字数ぶん削除する
            if (processed.Kind == ProcessedKind.Undo)
            {
                UndoLastInjection("音声コマンド");
                return;
            }

            // 「終了」「ストップ」: 声だけで音声入力を止める
            if (processed.Kind == ProcessedKind.Stop)
            {
                lock (_sync) StopListening("音声コマンド");
                return;
            }

            // キー操作の音声コマンド（送信・全選択など）は、文字ではなくキーを送る
            if (processed.Kind == ProcessedKind.Keys && processed.Keys is { } keys)
            {
                if (TextInjector.SendKeyStroke(keys))
                {
                    _log.Info($"音声コマンド「{processed.CommandName}」を実行しました");
                    // キー操作は文字数ぶんの BackSpace では取り消せないため、取り消しの対象から外す。
                    // 送信した後の入力は新しい文になるので、英語の単語間スペースも補わない
                    _lastInjectedLength = 0;
                    _lastInjectedApp = "";
                    _lastInjectedText = "";
                    _spaceBeforeNext = false;
                    ScheduleImeRestore();
                }
                else
                {
                    _log.Info($"入力欄が見つからないため音声コマンド「{processed.CommandName}」を実行しませんでした");
                }
                return;
            }

            if (processed.Text.Length == 0) return;

            if (processed.Kind == ProcessedKind.Command)
                _log.Info($"音声コマンド「{processed.CommandName}」を実行しました");
            else
                _log.Recognition(processed.Text);

            bool isCommand = processed.Kind == ProcessedKind.Command;
            bool spaced = RecognitionLanguages.UsesWordSpacing(_settings.Current.RecognitionLanguage);
            var window = TextInjector.GetForegroundWindowHandle();
            var output = spaced && !isCommand ? WithLeadingSpace(processed.Text, window) : processed.Text;

            var app = Inject(output, isCommand);
            if (spaced && app.Length > 0) RememberSpacing(output, isCommand, window);

            // 話した内容は、入力に失敗した場合も含めて履歴に残す。
            // 入力欄が無くて入らなかった文章も、あとから取り出せるようにするため。
            if (!isCommand) AddHistory(processed.Text, app);
        }));
    }

    /// <summary>続きの入力であれば、単語がくっつかないよう先頭にスペースを補う。</summary>
    private string WithLeadingSpace(string text, IntPtr window)
    {
        if (!_spaceBeforeNext || window != _spaceWindow || text.Length == 0) return text;
        // 句読点や閉じ括弧で始まる場合は、前の語に続けるのでスペースは入れない
        if (char.IsWhiteSpace(text[0]) || ".,!?;:)]}'\"".Contains(text[0])) return text;
        return " " + text;
    }

    /// <summary>入力した内容から、次の入力の前にスペースが要るかを覚えておく。</summary>
    private void RememberSpacing(string output, bool isCommand, IntPtr window)
    {
        _spaceWindow = window;
        if (output.Length == 0) return;
        char last = output[^1];
        _spaceBeforeNext = isCommand
            // 「@」「-」「/」のように語をつなぐ記号の後には空けない。句読点の後には空ける
            ? ".,!?;:".Contains(last)
            : !char.IsWhiteSpace(last);
    }

    private void AddHistory(string text, string app)
    {
        var record = new RecognitionRecord(DateTime.Now, text, app);
        lock (_sync)
        {
            _history.AddFirst(record);
            while (_history.Count > MaxHistory) _history.RemoveLast();
        }
        HistoryAdded?.Invoke(record);
    }

    /// <summary>認識履歴を消去する。取り消した文章の集計も、話した内容なので一緒に消す。</summary>
    public void ClearHistory()
    {
        lock (_sync) _history.Clear();
        _undone.Clear();
        HistoryCleared?.Invoke();
        UndoneChanged?.Invoke();
    }

    /// <summary>
    /// 取り消した文章を集計し、同じ文章を何度も取り消していれば辞書への登録を勧める。UI スレッドで呼ぶ。
    /// </summary>
    private void RecordUndone(string text)
    {
        bool suggest = _undone.Record(text, _settings.Current.SuggestFrequentUndo, out var entry);
        UndoneChanged?.Invoke();
        if (suggest) FrequentUndoDetected?.Invoke(entry);
    }

    /// <summary>
    /// 履歴の文字を、最後に音声入力したウィンドウへもう一度入力する。
    /// 履歴画面のボタンから呼ぶため、その時点では VoiceDock の画面が前面にある。
    /// 入力先を前面に戻してから送る。入力先が既に無い場合は false。
    /// </summary>
    public async Task<bool> ReinjectAsync(string text)
    {
        if (!TextInjector.TryActivateWindow(_lastTargetWindow)) return false;

        // 前面が切り替わり、入力欄にフォーカスが戻るのを待つ
        await Task.Delay(250);
        Inject(text, isCommand: false);
        _log.Info("履歴の文字をもう一度入力しました");
        return true;
    }

    /// <summary>
    /// テキストを前面アプリへ入力する。入力方式はアプリ別設定があればそれを優先する。
    /// 入力欄が無い等で失敗した場合は何もしない（エラー通知なし）。
    /// </summary>
    /// <returns>入力できたアプリの名前。入力欄が無く入らなかった場合は空。</returns>
    private string Inject(string text, bool isCommand)
    {
        var window = TextInjector.GetForegroundWindowHandle();
        var app = TextInjector.GetForegroundProcessName();
        if (app.Length > 0)
        {
            lock (_sync) _seenApps.Add(app);
        }

        var method = ResolveInputMethod(app);

        // 改行やタブなどの操作系はクリップボード貼り付けに向かないため直接入力する
        bool ok = method == InputMethod.Clipboard && !isCommand
            ? TextInjector.SendViaClipboard(text)
            : TextInjector.SendText(text, ResolveNewlineMode(app));

        if (!ok)
        {
            _log.Info("テキスト入力欄が見つからないため流し込みをスキップしました");
            _lastInjectedLength = 0;
            _lastInjectedApp = "";
            _lastInjectedText = "";
            return "";
        }

        // 止めた後に届いた確定テキストや、履歴からの再入力でも IME をオフにしているため、
        // 入力し終えてから間を置いて戻す
        ScheduleImeRestore();

        _lastInjectedLength = CountTextElements(text);
        _lastInjectedApp = app;
        // 英語で補った先頭のスペースは、取り消した文章の集計には含めない
        _lastInjectedText = isCommand ? "" : text.Trim();
        _lastTargetWindow = window;
        LastTargetApp = app;
        return app;
    }

    /// <summary>絵文字・結合文字を 1 文字として数える（BackSpace の回数に合わせるため）。</summary>
    private static int CountTextElements(string text)
    {
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (enumerator.MoveNext()) count++;
        return count;
    }

    /// <summary>
    /// 動作チェック画面のテスト入力。音声入力と同じ経路（アプリ別の入力方式・改行、IME の扱い）で
    /// 前面の入力欄へ文字を入れる。入力先のアプリ名を返す（入らなかった場合は空）。
    /// 取り消しの対象にはしない（テストの文字を「とりけし」で消せても紛らわしいため）。
    /// </summary>
    public string InjectTest(string text)
    {
        var app = Inject(text, isCommand: false);
        _lastInjectedLength = 0;
        _lastInjectedApp = "";
        _lastInjectedText = "";
        return app;
    }

    /// <summary>アプリ別の改行の送り方の上書きがあればそれを、無ければ既定の送り方を返す。</summary>
    public NewlineMode ResolveNewlineMode(string appName)
    {
        var s = _settings.Current;
        if (appName.Length > 0 && s.AppNewlineModes.TryGetValue(appName, out var perApp))
            return perApp;
        return s.NewlineMode;
    }

    /// <summary>アプリ別の入力方式の上書きがあればそれを、無ければ既定の入力方式を返す。</summary>
    public InputMethod ResolveInputMethod(string appName)
    {
        var s = _settings.Current;
        if (appName.Length > 0 && s.AppInputMethods.TryGetValue(appName, out var perApp))
            return perApp;
        return s.InputMethod;
    }

    /// <summary>直前に入力したテキストを取り消す（入力した文字数ぶん BackSpace を送る）。</summary>
    public void UndoLastInjection(string reason)
    {
        _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (!_settings.Current.UndoEnabled) return;

            int count = _lastInjectedLength;
            if (count <= 0)
            {
                _log.Info("取り消せる入力がありません");
                ToastWindow.Show("取り消せる入力がありません。");
                return;
            }

            // 入力した直後に別のアプリへ移っている場合、そこで BackSpace を送ると
            // 無関係な文字を消してしまう。入力先が変わっていたら取り消さない。
            var current = TextInjector.GetForegroundProcessName();
            if (_lastInjectedApp.Length > 0 &&
                !string.Equals(current, _lastInjectedApp, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"入力先が変わっているため取り消しを中止しました（入力時: {_lastInjectedApp} / 現在: {current}）");
                ToastWindow.Show($"入力したアプリ（{_lastInjectedApp}）が前面にないため、取り消しを中止しました。", ToastKind.Warning);
                return;
            }

            bool undone = TextInjector.SendBackspaces(count);
            if (undone)
                _log.Info($"直前の入力 {count} 文字を取り消しました ({reason})");
            else
                _log.Info("取り消し先の入力欄が見つかりませんでした");

            var undoneText = _lastInjectedText;

            // 二重に取り消さないようクリアする
            _lastInjectedLength = 0;
            _lastInjectedApp = "";
            _lastInjectedText = "";

            if (undone && undoneText.Length > 0) RecordUndone(undoneText);
        }));
    }

    /// <summary>
    /// 実際の認識モードの通知。ローカル処理を希望したのに使えなかった場合は、
    /// 毎回うるさくならないよう起動後 1 回だけ通知する。
    /// </summary>
    private void OnRecognitionModeReported(string mode)
    {
        _dispatcher.BeginInvoke(() =>
        {
            switch (mode)
            {
                case "local":
                    if (!_localModeNotified)
                    {
                        _localModeNotified = true;
                        ToastWindow.Show("端末内で音声認識しています（音声はクラウドに送信されません）。");
                    }
                    _log.Info("認識モード: 端末内処理 (processLocally)");
                    break;
                case "downloading":
                    if (!_localFallbackNotified)
                    {
                        _localFallbackNotified = true;
                        ToastWindow.Show("端末内認識の言語パックを取得しています。完了までクラウド認識で動作します。", ToastKind.Warning);
                    }
                    _log.Info("認識モード: クラウド（端末内認識の言語パックを取得中）");
                    break;
                case "unsupported":
                    if (!_localFallbackNotified)
                    {
                        _localFallbackNotified = true;
                        ToastWindow.Show("このブラウザは端末内認識に対応していないため、クラウド認識で動作します。", ToastKind.Warning);
                    }
                    _log.Info("認識モード: クラウド（端末内認識は非対応）");
                    break;
                default:
                    _log.Info("認識モード: クラウド");
                    break;
            }
        });
    }

    private void OnRecognitionError(string detail)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // ログには毎回残すが、通知は種類ごとに間隔を空ける。
            // 認識エンジンは同じエラーを短時間に何度も返すことがあり、
            // そのたびに通知を出すと画面が埋まってしまう。
            switch (detail)
            {
                case "not-allowed":
                case "service-not-allowed":
                    _log.Error($"マイクの使用が許可されていません ({detail})");
                    SetErrorState();
                    if (ShouldNotify(detail))
                        ToastWindow.Show("マイクの使用が許可されていません。ブラウザのマイク権限を確認してください。", ToastKind.Error);
                    break;
                case "speech-unsupported":
                    _log.Error("このブラウザは Web Speech API に対応していません");
                    SetErrorState();
                    if (ShouldNotify(detail))
                        ToastWindow.Show("認識ブラウザが Web Speech API に対応していません。Edge/Chrome をご利用ください。", ToastKind.Error);
                    break;
                case "network":
                    _log.Warn("認識でネットワークエラーが発生しました");
                    if (ShouldNotify(detail))
                        ToastWindow.Show("音声認識のネットワークエラーが発生しました。接続を確認してください。", ToastKind.Warning);
                    break;
                default:
                    _log.Warn($"認識エラー: {detail}");
                    break;
            }
        });
    }

    /// <summary>同じ内容の通知を短時間に繰り返さないための判定。</summary>
    private bool ShouldNotify(string key)
    {
        var now = DateTime.UtcNow;
        if (_errorNoticedAtUtc.TryGetValue(key, out var last) && now - last < ErrorNoticeInterval)
            return false;
        _errorNoticedAtUtc[key] = now;
        return true;
    }

    private void SetErrorState()
    {
        _inErrorState = true;
        _tray.SetState(TrayState.Error);
    }

    /// <summary>
    /// エラー表示を解除する。一度赤くなったまま戻らないと、
    /// 問題が解決したあとも壊れているように見えてしまう。
    /// </summary>
    private void ClearErrorState()
    {
        if (!_inErrorState) return;
        _inErrorState = false;
        _errorNoticedAtUtc.Clear();
        _tray.SetState(IsListening ? TrayState.Recording : TrayState.Idle);
        _log.Info("認識できたため、エラー表示を解除しました");
    }

    private void ResetSilenceTimer()
    {
        _dispatcher.BeginInvoke(() =>
        {
            StopSilenceTimer();

            // 0 秒は「自動停止しない」
            int seconds = _settings.Current.SilenceAutoStopSeconds;
            if (seconds <= 0) return;

            _silenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _silenceTimer.Tick += (_, _) =>
            {
                lock (_sync)
                {
                    if (!_listening) return;
                    _log.Info($"{seconds} 秒間無音が続いたため音声入力を自動停止します");
                    StopListening("無音自動停止");
                }
            };
            _silenceTimer.Start();
        });
    }

    private void StopSilenceTimer()
    {
        _silenceTimer?.Stop();
        _silenceTimer = null;
    }

    /// <summary>音声入力中でなければ、少し待ってから IME を元に戻すよう予約する（予約済みなら延ばす）。</summary>
    private void ScheduleImeRestore()
    {
        lock (_sync) if (_listening) return;
        _imeRestoreTimer.Stop();
        _imeRestoreTimer.Start();
    }

    public void Dispose()
    {
        StopSilenceTimer();
        // 終了時にオフのまま残さない
        _imeRestoreTimer.Stop();
        TextInjector.RestoreSuppressedIme();
    }
}
