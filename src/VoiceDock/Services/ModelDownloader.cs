using System.IO;
using System.Net.Http;

namespace VoiceDock.Services;

/// <summary>
/// Whisper (ggml) モデルの取得。初回起動時（またはモデルサイズ変更時）に
/// Hugging Face から自動ダウンロードして %APPDATA%\VoiceDock\models に保存する。
/// </summary>
public sealed class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static readonly string[] ModelSizes = { "tiny", "base", "small", "medium", "large-v3" };

    public ModelDownloader(LogService log)
    {
        _log = log;
    }

    public static string GetModelPath(string modelSize) =>
        Path.Combine(AppPaths.ModelsDir, $"ggml-{modelSize}.bin");

    public static bool IsModelReady(string modelSize) => File.Exists(GetModelPath(modelSize));

    /// <summary>
    /// モデルファイルが無ければダウンロードし、モデルファイルのパスを返す。
    /// progress には 0..1 のダウンロード進捗を通知する（サイズ不明時は通知しない）。
    /// </summary>
    public async Task<string> EnsureModelAsync(string modelSize, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var path = GetModelPath(modelSize);
        if (File.Exists(path)) return path;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path)) return path;

            AppPaths.EnsureDirectories();
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelSize}.bin";
            _log.Info($"モデル {modelSize} のダウンロードを開始します: {url}");

            var tmp = path + ".download";
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(tmp);

                var buffer = new byte[1024 * 1024];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (total is > 0) progress?.Report((double)readTotal / total.Value);
                }
            }

            File.Move(tmp, path, overwrite: true);
            _log.Info($"モデル {modelSize} のダウンロードが完了しました ({new FileInfo(path).Length / 1024 / 1024} MB)");
            return path;
        }
        catch
        {
            try { File.Delete(path + ".download"); } catch { /* 掃除失敗は無視 */ }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
