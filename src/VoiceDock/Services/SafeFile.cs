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
    /// 内容が正しく解釈できるかの判定。JSON として解析できるかだけでなく、
    /// 実際に目的の型へ変換できるかまで確かめること。ここが緩いと、
    /// 「JSON としては正しいが中身が壊れている」ファイルで .bak への切り替えが働かない。
    /// </param>
    public static string? ReadAllText(string path, Func<string, bool>? validate = null)
        => ReadAllText(path, validate, out _);

    /// <param name="fromBackup">.bak から復帰した場合 true。</param>
    /// <inheritdoc cref="ReadAllText(string, Func{string, bool})"/>
    public static string? ReadAllText(string path, Func<string, bool>? validate, out bool fromBackup)
    {
        fromBackup = false;
        var backup = path + BackupSuffix;

        foreach (var candidate in new[] { path, backup })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = File.ReadAllText(candidate);
                if (validate != null && !validate(text)) continue;

                if (candidate == backup)
                {
                    fromBackup = true;
                    // 壊れた本体をそのままにしておくと、次の保存で .bak が
                    // 壊れた内容に置き換わり、無事だった控えを失ってしまう。
                    // 復帰した内容をすぐ本体へ書き戻して、両方を正常な状態に揃える。
                    TryRestore(backup, path);
                }
                return text;
            }
            catch
            {
                // 次の候補（.bak）を試す
            }
        }
        return null;
    }

    /// <summary>.bak の内容を本体へ書き戻す（失敗しても読み込み自体は成功扱いにする）。</summary>
    private static void TryRestore(string backup, string path)
    {
        try
        {
            File.Copy(backup, path, overwrite: true);
        }
        catch
        {
            // 書き戻せなくても、読み込んだ内容は使える
        }
    }
}
