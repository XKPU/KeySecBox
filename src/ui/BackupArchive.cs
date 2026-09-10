using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KeySecBox;

/// <summary>
/// 目录归档：把一组文件按相对路径打包为单个字节流，供加密备份整体封装。
/// 布局：magic(8) | 条目数(4) | [nameLen(4) | name(UTF8) | dataLen(8) | data]…
/// 相对路径统一以 '/' 分隔，便于跨平台还原。
/// </summary>
internal static class BackupArchive
{
    public const string Magic = "KSBXARC1";

    /// <summary>把 <paramref name="rootDir"/> 下的 <paramref name="relativePaths"/> 打包为字节流。</summary>
    public static byte[] Pack(string rootDir, IEnumerable<string> relativePaths)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes(Magic));
            bw.Write(0); // 条目数占位，末尾回填

            int count = 0;
            foreach (var rel in relativePaths)
            {
                var full = Path.Combine(rootDir, rel);
                if (!File.Exists(full)) continue;

                var name = Encoding.UTF8.GetBytes(rel.Replace('\\', '/'));
                bw.Write(name.Length);
                bw.Write(name);

                using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                bw.Write(src.Length); // long = 8 字节
                src.CopyTo(ms);
                count++;
            }

            bw.Flush();
            long end = ms.Position;
            ms.Position = Magic.Length; // 回填条目数
            bw.Write(count);
            bw.Flush();
            ms.Position = end;
        }
        return ms.ToArray();
    }
}
