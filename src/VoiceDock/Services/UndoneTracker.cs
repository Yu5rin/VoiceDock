namespace VoiceDock.Services;

/// <summary>取り消した文章と、その回数。誤認識しやすい語を見つけて辞書に登録してもらうために集計する。</summary>
public sealed record UndoneText(string Text, int Count, DateTime LastTime);

/// <summary>
/// 取り消した文章の集計。ひらがな・カタカナ・全角半角・末尾の句読点の違いは同じ文章として数える。
/// 同じ文章を何度も取り消していれば誤認識しやすい語とみなし、1 つの文章につき 1 回だけ登録を勧める。
/// 話した内容を含むため、メモリにだけ持ちファイルには保存しない（認識履歴と同じ扱い）。
/// </summary>
public sealed class UndoneTracker
{
    /// <summary>同じ文章をこの回数取り消したら、辞書への登録を勧める。</summary>
    public const int SuggestThreshold = 2;

    /// <summary>集計する最大件数。超えたら最後に取り消したのが古いものから捨てる。</summary>
    public const int MaxEntries = 100;

    private readonly object _sync = new();
    private readonly Dictionary<string, UndoneText> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _suggested = new(StringComparer.Ordinal);

    /// <summary>
    /// 取り消した文章を 1 回分数える。登録を勧めるべきなら true を返す
    /// （しきい値に達し、まだその文章で勧めていない場合）。
    /// </summary>
    public bool Record(string text, bool suggestEnabled, out UndoneText entry, DateTime? now = null)
    {
        entry = new UndoneText(text, 0, now ?? DateTime.Now);
        var key = KanaNormalizer.NormalizeKey(text.Trim().TrimEnd('。', '、', '.', ',', '！', '!', '？', '?'));
        if (key.Length == 0) return false;

        lock (_sync)
        {
            int count = _entries.TryGetValue(key, out var old) ? old.Count + 1 : 1;
            entry = new UndoneText(text, count, now ?? DateTime.Now);
            _entries[key] = entry;

            while (_entries.Count > MaxEntries)
                _entries.Remove(_entries.MinBy(p => p.Value.LastTime).Key);

            return suggestEnabled && count >= SuggestThreshold && _suggested.Add(key);
        }
    }

    /// <summary>集計（回数の多い順、同じ回数なら新しい順）。</summary>
    public IReadOnlyList<UndoneText> Snapshot()
    {
        lock (_sync)
            return _entries.Values.OrderByDescending(u => u.Count).ThenByDescending(u => u.LastTime).ToList();
    }

    /// <summary>集計と「勧めた」記録をすべて消す。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _suggested.Clear();
        }
    }
}
