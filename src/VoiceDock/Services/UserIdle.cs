using System.Runtime.InteropServices;

namespace VoiceDock.Services;

/// <summary>PC の操作（キーボード・マウス）が最後にあってからの時間。</summary>
public static class UserIdle
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>最後の操作からの経過時間。取得できなければ 0（＝操作中とみなし、入れ替えを見送る側に倒す）。</summary>
    public static TimeSpan SinceLastInput()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // どちらも起動からのミリ秒（49.7 日で一周する）。符号なしの引き算で一周しても正しく差が出る
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }
}
