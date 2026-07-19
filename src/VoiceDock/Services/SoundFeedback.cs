using System.IO;
using System.Runtime.InteropServices;

namespace VoiceDock.Services;

/// <summary>
/// 録音開始/停止時の操作音。外部ファイルに依存しないよう、
/// 短いサイン波 WAV をメモリ上で生成し、winmm の PlaySound (SND_MEMORY) で再生する。
/// </summary>
public static class SoundFeedback
{
    // SND_ASYNC 再生中もバッファが有効であるよう、プロセス生存中はピン留めして保持する
    private static readonly GCHandle StartWav = Pin(CreateWav(880, 90));
    private static readonly GCHandle StopWav = Pin(CreateWav(587, 90));

    public static void PlayStart() => Play(StartWav);

    public static void PlayStop() => Play(StopWav);

    private static void Play(GCHandle wav)
    {
        try
        {
            PlaySound(wav.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch
        {
            // 再生失敗は無視
        }
    }

    private static GCHandle Pin(byte[] data) => GCHandle.Alloc(data, GCHandleType.Pinned);

    private static byte[] CreateWav(double frequency, int durationMs)
    {
        const int sampleRate = 22050;
        int samples = sampleRate * durationMs / 1000;
        int dataSize = samples * 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // モノラル
        w.Write(sampleRate);
        w.Write(sampleRate * 2);      // byte rate
        w.Write((short)2);            // block align
        w.Write((short)16);           // bits
        w.Write("data"u8);
        w.Write(dataSize);

        for (int i = 0; i < samples; i++)
        {
            // フェードイン/アウトでクリック音を防ぐ
            double t = (double)i / samples;
            double envelope = Math.Min(1.0, Math.Min(t, 1.0 - t) * 10);
            double v = Math.Sin(2 * Math.PI * frequency * i / sampleRate) * envelope * 0.25;
            w.Write((short)(v * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
