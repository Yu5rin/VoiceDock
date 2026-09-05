using System.IO;
using System.Text;

namespace VoiceDock.Services;

/// <summary>
/// 設定・辞書・定型文の保存に使うファイル入出力。
///
/// 直接上書きすると、書き込み中に強制終了や電源断が起きた場合にファイルが壊れ、
/// 次回起動時に設定や辞書がまるごと失われる。それを避けるため、
/// 「一時ファイルへ書く → 既存を .bak へ退避しつつ差し替える」という手順を踏む。
/// 読み込み時は、本体が壊れていれば .bak から復帰を試みる。
/// </summary>
public static class SafeFile
{
    private const string TempSuffix = ".tmp";
    private const string BackupSuffix = ".bak";

    /// <summary>
    /// 内容を原子的に書き込む。書き込み前の内容は .bak として 1 世代保持する。
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var temp = path + TempSuffix;
        var backup = path + BackupSuffix;

        // まず一時ファイルへ完全に書き出し、ディスクへ確実に送る
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // 既存を .bak へ退避しつつ差し替える（OS 側で原子的に行われる）
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// 内容を読み込む。本体が読めない・壊れている場合は .bak から復帰を試みる。
    /// どちらも読めない場合は null。
    /// </summary>
    /// <param name="validate">
    /// 内容が正しく解釈できるかの判定（JSON として解析できるか等）。
    /// false を返した場合は破損とみなして .bak を試す。
    /// </param>
    public static string? ReadAllText(string path, Func<string, bool>? validate = null)
    {
        foreach (var candidate in new[] { path, path + BackupSuffix })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate);
                if (validate != null && !validate(text)) continue;
                return text;
            }
            catch
            {
                // 次の候補（.bak）を試す
            }
        }
        return null;
    }

    /// <summary>本体が壊れていて .bak から復帰したかどうかを判定する（通知用）。</summary>
    public static bool IsBackupUsable(string path, Func<string, bool> validate)
    {
        try
        {
            var backup = path + BackupSuffix;
            return File.Exists(backup) && validate(File.ReadAllText(backup));
        }
        catch
        {
            return false;
        }
    }
}
