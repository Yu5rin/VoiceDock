using VoiceDock.Models;
using VoiceDock.Services;

// 作業用フォルダに保存先を向ける（本物の設定を触らない）
var home = Path.Combine(Path.GetTempPath(), "vd-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(home);
Environment.SetEnvironmentVariable("APPDATA", home);
AppPaths.TestRoot = Path.Combine(home, "data");

int ng = 0, ok = 0;
void Check(string label, bool pass, string detail = "")
{
    if (pass) ok++; else ng++;
    if (!pass) Console.WriteLine($"NG  {label}{(detail.Length > 0 ? $"  … {detail}" : "")}");
}
void Same(string label, string actual, string expected) => Check(label, actual == expected, $"実際: {actual}");

var log = new LogService();
var settings = new SettingsService(log); settings.Load();
var dict = new DictionaryService(log); dict.Load();
var snip = new SnippetService(log); snip.Load();
var proc = new TextProcessor(settings, dict, snip);

// ── 保存先の引っ越し ──
var legacy = Path.Combine(home, "legacy"); var portable = Path.Combine(home, "portable-data");
Directory.CreateDirectory(Path.Combine(legacy, "logs")); Directory.CreateDirectory(Path.Combine(legacy, "browser-profile"));
File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"Hotkey\":\"Ctrl+Alt+V\"}");
File.WriteAllText(Path.Combine(legacy, "settings.json.bak"), "{}");
File.WriteAllText(Path.Combine(legacy, "dictionary.json"), "[]");
File.WriteAllText(Path.Combine(legacy, "logs", "voicedock-20261001.log"), "log");
File.WriteAllText(Path.Combine(legacy, "browser-profile", "Local State"), "x");
int moved = AppPaths.MigrateFrom(legacy, portable);
Check("引っ越し: 設定・辞書・ログを写す", moved == 4 && File.ReadAllText(Path.Combine(portable, "settings.json")).Contains("Ctrl+Alt+V")
    && File.Exists(Path.Combine(portable, "logs", "voicedock-20261001.log")), moved.ToString());
Check("引っ越し: ブラウザのプロファイルは写さない", !Directory.Exists(Path.Combine(portable, "browser-profile")));
Check("引っ越し: 元のファイルは残す", File.Exists(Path.Combine(legacy, "settings.json")));
File.WriteAllText(Path.Combine(legacy, "snippets.json"), "[]");
Check("引っ越し: 設定がすでにあれば何もしない（上書きしない）", AppPaths.MigrateFrom(legacy, portable) == 0 && !File.Exists(Path.Combine(portable, "snippets.json")));
Check("引っ越し: 同じ場所なら何もしない", AppPaths.MigrateFrom(legacy, legacy) == 0);
Check("引っ越し: 以前の場所が無ければ何もしない", AppPaths.MigrateFrom(Path.Combine(home, "none"), Path.Combine(home, "x")) == 0);
Check("書き込めるフォルダの判定", AppPaths.CanWrite(Path.Combine(home, "w")) && !AppPaths.CanWrite("/proc/voicedock-test"));
settings.Update(s => s.Hotkey = "Ctrl+Alt+Space");
Check("保存は差し替えた保存先に行く", AppPaths.SettingsFile == Path.Combine(home, "data", "settings.json") && File.Exists(AppPaths.SettingsFile));
var reloaded = new SettingsService(log); reloaded.Load();
Check("保存先から読み直せる", reloaded.Current.Hotkey == "Ctrl+Alt+Space");
var realRoot = AppPaths.TestRoot; AppPaths.TestRoot = null;
Check("通常は exe の隣の data フォルダ", AppPaths.Root == Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "data") && AppPaths.IsPortable, AppPaths.Root);
AppPaths.TestRoot = realRoot;

// ── 文章の後処理 ──
settings.Update(s => { s.RecognitionLanguage = "ja-JP"; s.AutoPeriod = false; s.RemoveRecognizerPunctuation = true;
    s.QuestionByIntonation = true; s.KanjiToArabic = true; s.RemoveFillers = false; s.CharacterWidth = CharacterWidth.AsIs;
    s.VoiceCommandsEnabled = true; s.KeyCommandsEnabled = true; s.UndoEnabled = true; s.SnippetsEnabled = true; });
Same("句読点を取り除く", proc.Process("はい、わかりました。").Text, "はいわかりました");
Same("語尾上がりで「？」", proc.Process("明日行く。", true).Text, "明日行く？");
Same("漢数字", proc.Process("三時半に集合").Text, "3時半に集合");
Same("漢数字: 言葉は変えない", proc.Process("一緒に一人で").Text, "一緒に一人で");
Same("漢数字: はっきりしない数は変えない", proc.Process("数十人が何百回も来て十数年たった").Text, "数十人が何百回も来て十数年たった");
Same("漢数字: 決まった言い回しは変えない", proc.Process("二日酔いで二度と飲まない").Text, "二日酔いで二度と飲まない");
Same("漢数字: 万・億は単位を残す", proc.Process("三億五千万円と十万人").Text, "3億5000万円と10万人");
Same("コマンド「まる」", proc.Process("まる").Text, "。");
Same("コマンド「点」", proc.Process("点").Text, "、");
Check("取り消し「戻して」", proc.Process("戻して").Kind == ProcessedKind.Undo);
Check("停止「終了」", proc.Process("終了").Kind == ProcessedKind.Stop);
Check("キー操作「送信」", proc.Process("送信").Kind == ProcessedKind.Keys);
Check("文中の「終了」は文字", proc.Process("会議は終了しました").Kind == ProcessedKind.Text);
dict.Replace(new[] { new DictionaryEntry { Wrong = "やまだ", Correct = "山田" },
    new DictionaryEntry { Wrong = "りょうかい", Correct = "了解しました。", WholeOnly = true } });
Same("辞書: カタカナにも当たる", proc.Process("ヤマダさん").Text, "山田さん");
Same("辞書: 全体のみ", proc.Process("りょうかい").Text, "了解しました。");
Same("辞書: 全体のみは文中で置き換えない", proc.Process("りょうかいです").Text, "りょうかいです");
snip.Replace(new[] { new SnippetEntry { Phrase = "しょめい", Expansion = "山田、太郎" } });
Same("定型文（句読点は残す）", proc.Process("しょめい").Text, "山田、太郎");
settings.Update(s => s.RecognitionLanguage = "en-US");
Same("英語の句読点はそのまま", proc.Process("Well, it costs 3.5 dollars").Text, "Well, it costs 3.5 dollars");
settings.Update(s => s.RecognitionLanguage = "ja-JP");
var t = new UndoneTracker();
Check("よく取り消す文章: 2 回目で勧める", !t.Record("やまだ", true, out _) && t.Record("ヤマダ", true, out _));
Same("更新: ダウンロード先の組み立て", UpdateFeed.TryBuildDownloadUrl("https://api.github.com/repos/Yu5rin/VoiceDock/releases/latest", "v0.7.12") ?? "",
    "https://github.com/Yu5rin/VoiceDock/releases/download/v0.7.12/VoiceDock-v0.7.12-win-x64.exe");
Check("自動更新: 使っていなければ入れ替える", IdleUpdatePolicy.WhyNotNow(false, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), false) == null);

// ── 認識用ブラウザのプロファイル判定 ──
const string prof = @"C:\追加\VoiceDock\data\browser-profile";
Check("プロファイル: 引用符あり・日本語のパス", BrowserLauncher.UsesProfile(@"""msedge.exe"" --app=http://127.0.0.1:1/ ""--user-data-dir=C:\追加\VoiceDock\data\browser-profile"" --no-first-run", prof));
Check("プロファイル: 値だけ引用符", BrowserLauncher.UsesProfile(@"msedge.exe --type=renderer --user-data-dir=""C:\追加\VoiceDock\data\browser-profile"" --lang=ja", prof));
Check("プロファイル: 引用符なし", BrowserLauncher.UsesProfile(@"msedge.exe --user-data-dir=C:\追加\VoiceDock\data\browser-profile --no-first-run", prof));
Check("プロファイル: 大文字小文字・末尾の区切り", BrowserLauncher.UsesProfile(@"msedge.exe --user-data-dir=c:\追加\voicedock\DATA\browser-profile\ --type=renderer", prof));
Check("プロファイル: 普段使いの Edge は対象外", !BrowserLauncher.UsesProfile(@"msedge.exe --profile-directory=Default", prof));
Check("プロファイル: 別のフォルダは対象外", !BrowserLauncher.UsesProfile(@"msedge.exe --user-data-dir=C:\追加\VoiceDock\data\browser-profile2", prof));
Check("プロファイル: 上位のフォルダは対象外", !BrowserLauncher.UsesProfile(@"msedge.exe --user-data-dir=C:\追加\VoiceDock\data", prof));
Check("プロファイル: 起動引数が読めない場合は対象外", !BrowserLauncher.UsesProfile(null, prof));

// ── マイクの選択 ──
Check("マイク: 既定は Windows の既定", new AppSettings().MicrophoneDeviceId == "");
settings.Update(s => { s.MicrophoneDeviceId = "a1B2+/=_-c3"; s.MicrophoneLabel = "USB マイク"; });
var micReload = new SettingsService(log); micReload.Load();
Check("マイク: 選んだマイクを保存して読み直せる", micReload.Current.MicrophoneDeviceId == "a1B2+/=_-c3" && micReload.Current.MicrophoneLabel == "USB マイク");
settings.Update(s => { s.MicrophoneDeviceId = "x\",\"evil\":\"1"; s.MicrophoneLabel = "変"; });
Check("マイク: 想定外の文字を含む装置 ID は既定に戻す", settings.Current.MicrophoneDeviceId == "" && settings.Current.MicrophoneLabel == "");
settings.Update(s => { s.MicrophoneDeviceId = ""; s.MicrophoneLabel = ""; });

// ── 壊れた・手で編集したファイルへの備え ──
File.WriteAllText(AppPaths.SettingsFile, "{\"Hotkey\":null,\"UndoHotkey\":null,\"MicrophoneDeviceId\":null,\"MicrophoneLabel\":null," +
    "\"RecognitionLanguage\":null,\"WindowPlacements\":null,\"SilenceAutoStopSeconds\":60,\"OverlayPosition\":99}");
var nullSettings = new SettingsService(log); nullSettings.Load();
Check("設定: null が書かれていても、ほかの値は生かして読む",
    !nullSettings.ResetBecauseUnreadable && nullSettings.Current.SilenceAutoStopSeconds == 60
    && nullSettings.Current.Hotkey == "Ctrl+Space" && nullSettings.Current.MicrophoneDeviceId == ""
    && nullSettings.Current.WindowPlacements != null && nullSettings.Current.OverlayPosition == OverlayPosition.BottomCenter);
settings.Update(s => s.SilenceAutoStopSeconds = 30);

File.WriteAllText(AppPaths.DictionaryFile, "[null,{\"Wrong\":null,\"Correct\":\"x\"},{\"Wrong\":\"さとう\",\"Correct\":\"佐藤\"}]");
var nullDict = new DictionaryService(log); nullDict.Load();
Same("辞書: null の項目があっても落ちずに置き換える", nullDict.ApplyReplacements("サトウさん"), "佐藤さん");
dict.Replace(dict.Entries);   // 元の内容で書き戻す

// ── CSV の文字コード ──
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var sjisCsv = Path.Combine(home, "sjis.csv");
File.WriteAllBytes(sjisCsv, System.Text.Encoding.GetEncoding(932).GetBytes("読み,表記\r\nやまだ,山田\r\n"));
Check("CSV: Excel が保存した Shift-JIS も文字化けせずに読む", Csv.ReadText(sjisCsv).Contains("やまだ,山田"));
var utf8Csv = Path.Combine(home, "utf8.csv");
File.WriteAllText(utf8Csv, "読み,表記\r\nやまだ,山田\r\n", new System.Text.UTF8Encoding(true));
Check("CSV: UTF-8（BOM 付き）を読む", Csv.ReadText(utf8Csv).StartsWith("読み,表記"));

// ── 一時的に読めないファイル ──
var busy = Path.Combine(home, "busy.json");
SafeFile.WriteAllText(busy, "old"); SafeFile.WriteAllText(busy, "new");
using (var hold = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    _ = Task.Run(async () => { await Task.Delay(200); hold.Dispose(); });
    var read = SafeFile.ReadAllText(busy, null, out bool fromBak);
    Check("ほかのソフトが一時的に掴んでいても、控えへ切り替えずに読み直す", read == "new" && !fromBak, $"実際: {read} / 控え={fromBak}");
}

// バックアップの往復も新しい保存先で動く
var bak = Path.Combine(home, "backup.json");
new BackupService(settings, dict, snip, log).Save(bak);
Check("バックアップを保存・読み込みできる", File.Exists(bak) && BackupService.Load(bak).Dictionary.Count == 2);

Directory.Delete(home, true);
Console.WriteLine($"OK {ok} 件 / NG {ng} 件");
Console.WriteLine(ng == 0 ? "=== すべて合格 ===" : $"=== {ng} 件 失敗 ===");
return ng == 0 ? 0 : 1;
