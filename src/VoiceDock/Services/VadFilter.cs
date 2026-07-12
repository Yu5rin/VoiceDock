using System.Linq;

namespace VoiceDock.Services;

/// <summary>
/// VAD フィルタ（無音区間除去）。フレーム単位のエネルギー判定で
/// 無音フレームを取り除き、発話区間の前後にはパディングを残す。
/// </summary>
public static class VadFilter
{
    private const int FrameMs = 30;
    /// <summary>発話とみなす RMS の下限</summary>
    private const float EnergyThreshold = 0.008f;
    /// <summary>発話区間の前後に残すフレーム数 (30ms × 10 = 300ms)</summary>
    private const int PaddingFrames = 10;

    /// <summary>
    /// 無音区間を除去したサンプル列を返す。全区間が無音の場合は空配列を返す。
    /// </summary>
    public static float[] Trim(float[] samples, int sampleRate)
    {
        if (samples.Length == 0) return samples;

        int frameSize = sampleRate * FrameMs / 1000;
        int frameCount = (samples.Length + frameSize - 1) / frameSize;
        if (frameCount <= 1) return samples;

        var isSpeech = new bool[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            int start = f * frameSize;
            int len = Math.Min(frameSize, samples.Length - start);
            double sum = 0;
            for (int i = 0; i < len; i++)
            {
                float v = samples[start + i];
                sum += v * v;
            }
            isSpeech[f] = Math.Sqrt(sum / len) >= EnergyThreshold;
        }

        if (!isSpeech.Any(s => s)) return Array.Empty<float>();

        // 発話フレームの前後にパディングを付けて残す
        var keep = new bool[frameCount];
        for (int f = 0; f < frameCount; f++)
        {
            if (!isSpeech[f]) continue;
            int from = Math.Max(0, f - PaddingFrames);
            int to = Math.Min(frameCount - 1, f + PaddingFrames);
            for (int k = from; k <= to; k++) keep[k] = true;
        }

        var result = new List<float>(samples.Length);
        for (int f = 0; f < frameCount; f++)
        {
            if (!keep[f]) continue;
            int start = f * frameSize;
            int len = Math.Min(frameSize, samples.Length - start);
            for (int i = 0; i < len; i++) result.Add(samples[start + i]);
        }
        return result.ToArray();
    }
}
