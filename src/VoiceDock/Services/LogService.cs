using System.IO;
using System.Linq;
using System.Text;

namespace VoiceDock.Services;

public enum LogLevel { Info, Warn, Error, Recognition }

public record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public string LevelLabel => Level switch
    {
        LogLevel.Info => "動作",
        LogLevel.Warn => "警告",
        LogLevel.Error => "エラー",
        LogLevel.Recognition => "認識",
        _ => "?"
    };

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{LevelLabel}] {Message}";
}

/// <summary>
/// ログ機能。エラー・動作ログと認識テキスト履歴を
/// %APPDATA%\VoiceDock\logs に日別ファイルで永続化し、30日経過分を自動削除する。
/// 画面表示用に直近のエントリをメモリにも保持する。
/// </summary>
public sealed class LogService
{
    private const int MaxMemoryEntries = 2000;
    private const int RetentionDays = 30;

    /// <summary>1 日分のログファイルの上限。超えたら .1 へ退避して新しく書き始める。</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly LinkedList<LogEntry> _recent = new();

    /// <summary>
    /// 認識テキストをファイルに保存するかどうか。
    /// 音声入力の内容そのものが平文で残るため、既定では保存しない
    /// （画面のログ一覧には出るが、ファイルには書かない）。
    /// </summary>
    public bool PersistRecognitionText { get; set; }

    public event Action<LogEntry>? EntryAdded;

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Warn(string message) => Add(LogLevel.Warn, message);
    public void Error(string message) => Add(LogLevel.Error, message);
    public void Recognition(string text) => Add(LogLevel.Recognition, text);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync) return _recent.ToList();
    }

    private void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_sync)
        {
            _recent.AddLast(entry);
            while (_recent.Count > MaxMemoryEntries) _recent.RemoveFirst();

            // 認識テキストは音声入力の内容そのものなので、明示的に許可された場合だけ保存する
            if (level == LogLevel.Recognition && !PersistRecognitionText) { }
            else
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogsDir);
                    var file = Path.Combine(AppPaths.LogsDir, $"voicedock-{entry.Time:yyyyMMdd}.log");
                    RotateIfTooLarge(file);
                    File.AppendAllText(file, entry + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // ログ書き込み失敗でアプリを止めない
                }
            }
        }
        EntryAdded?.Invoke(entry);
    }

    /// <summary>1 ファイルが大きくなりすぎた場合、1 世代だけ退避して書き直す。</summary>
    private static void RotateIfTooLarge(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MaxFileBytes) return;
            var rotated = file + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(file, rotated);
        }
        catch
        {
            // 退避できなくても書き込みは続行する
        }
    }

    /// <summary>保存済みのログファイルをすべて削除する（画面からの手動操作用）。</summary>
    public int DeleteAllLogFiles()
    {
        lock (_sync)
        {
            int deleted = 0;
            try
            {
                if (!Directory.Exists(AppPaths.LogsDir)) return 0;
                foreach (var file in Directory.GetFiles(AppPaths.LogsDir, "voicedock-*.log*"))
                {
                    try { File.Delete(file); deleted++; }
                    catch { /* 使用中のファイルは飛ばす */ }
                }
            }
            catch
            {
                // 列挙に失敗しても画面は動かす
            }
            return deleted;
        }
    }

    /// <summary>画面に表示している履歴を消す（ファイルには触れない）。</summary>
    public void ClearMemory()
    {
        lock (_sync) _recent.Clear();
    }

    /// <summary>保存期間(30日)を過ぎたログファイルを削除する。起動時に呼ぶ。</summary>
    public void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            var limit = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(AppPaths.LogsDir, "voicedock-*.log*"))
            {
                var name = Path.GetFileName(file)["voicedock-".Length..];
                name = "voicedock-" + (name.Length >= 8 ? name[..8] : name);
                var datePart = name["voicedock-".Length..];
                if (DateTime.TryParseExact(datePart, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var date) && date < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 削除失敗は無視
        }
    }
}
