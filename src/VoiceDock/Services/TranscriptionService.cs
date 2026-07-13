using System.Text;
using Whisper.net;

namespace VoiceDock.Services;

/// <summary>
/// Whisper.net による文字起こし（バッチ処理・日本語固定）。
/// 辞書の登録単語を initial_prompt のヒントとして渡し、認識後に強制置換を適用する。
/// GPU/CPU の判定は Whisper.net のランタイム解決に委ねる（本ビルドは CPU ランタイム同梱）。
/// </summary>
public sealed class TranscriptionService : IDisposable
{
    private readonly LogService _log;
    private readonly DictionaryService _dictionary;
    private readonly object _sync = new();

    private WhisperFactory? _factory;
    private string? _loadedModelPath;

    public TranscriptionService(LogService log, DictionaryService dictionary)
    {
        _log = log;
        _dictionary = dictionary;
    }

    /// <summary>
    /// 16kHz mono float サンプルを文字起こしして整形済みテキストを返す。
    /// 認識できる音声が無い場合は空文字を返す。
    /// </summary>
    public async Task<string> TranscribeAsync(float[] samples, string modelPath,
        CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0) return "";

        var factory = GetFactory(modelPath);

        // 句読点の自動挿入を促す文＋辞書単語を initial_prompt ヒントとして渡す
        var prompt = new StringBuilder("以下は、句読点を含む自然な日本語の文章です。");
        var hint = _dictionary.BuildPromptHint();
        if (hint.Length > 0) prompt.Append($"次の語が含まれることがあります: {hint}。");

        // CPU 実行時の速度改善のため、利用可能なコアをできるだけ使う（最低 1）
        int threads = Math.Max(1, Environment.ProcessorCount - 1);

        await using var processor = factory.CreateBuilder()
            .WithLanguage("ja")
            .WithThreads(threads)
            .WithPrompt(prompt.ToString())
            .Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
            sb.Append(segment.Text);

        var text = sb.ToString().Trim();
        text = _dictionary.ApplyReplacements(text);
        return text;
    }

    private WhisperFactory GetFactory(string modelPath)
    {
        lock (_sync)
        {
            if (_factory != null && _loadedModelPath == modelPath) return _factory;

            _factory?.Dispose();
            _factory = null;
            _log.Info($"モデルを読み込みます: {modelPath}");
            _factory = WhisperFactory.FromPath(modelPath);
            _loadedModelPath = modelPath;
            return _factory;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}
