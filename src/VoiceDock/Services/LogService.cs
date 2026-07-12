using System.IO;
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

    private readonly object _sync = new();
    private readonly LinkedList<LogEntry> _recent = new();

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
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var file = Path.Combine(AppPaths.LogsDir, $"voicedock-{entry.Time:yyyyMMdd}.log");
                File.AppendAllText(file, entry + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ログ書き込み失敗でアプリを止めない
            }
        }
        EntryAdded?.Invoke(entry);
    }

    /// <summary>保存期間(30日)を過ぎたログファイルを削除する。起動時に呼ぶ。</summary>
    public void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LogsDir)) return;
            var limit = DateTime.Now.Date.AddDays(-RetentionDays);
            foreach (var file in Directory.GetFiles(AppPaths.LogsDir, "voicedock-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file); // voicedock-yyyyMMdd
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
