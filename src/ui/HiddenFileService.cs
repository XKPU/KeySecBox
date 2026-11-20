// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;

namespace KeySecBox;

/// <summary>
/// 文件/文件夹属性隐藏：等价于 <c>attrib +s +h +r "路径"</c> 与 <c>attrib -s -h -r "路径"</c>。
/// 加上系统 + 隐藏属性后资源管理器即使勾选"显示隐藏的项目"也不会列出该对象，
/// 只读属性则让误删先失败一次。恢复时只清除这三项，其余属性保持原样。
/// </summary>
internal static class HiddenFileService
{
    public const FileAttributes HideFlags =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;

    public static bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    public static FileAttributes Read(string path) => File.GetAttributes(path);

    /// <summary>隐藏状态以磁盘属性为准：同时具备隐藏与系统属性。</summary>
    public static bool IsHidden(string path)
    {
        if (!Exists(path)) return false;
        var attributes = Read(path);
        return (attributes & FileAttributes.Hidden) != 0
            && (attributes & FileAttributes.System) != 0;
    }

    /// <summary>隐藏并返回操作前的属性；失败抛出 IO 异常由调用方提示。</summary>
    public static FileAttributes Hide(string path)
    {
        var original = Read(path);
        File.SetAttributes(path, original | HideFlags);
        if (!IsHidden(path))
            throw new IOException("文件属性未生效，可能被其他进程占用。");
        return original;
    }

    /// <summary>恢复显示（清除隐藏 / 系统 / 只读），其余属性保持不变。</summary>
    public static void Restore(string path)
    {
        if (!Exists(path)) return;
        var current = Read(path);
        File.SetAttributes(path, current & ~HideFlags);
    }

    /// <summary>路径是否形如 <c>C:\…</c> 或 <c>\\server\share\…</c>，用于录入前的粗校验。</summary>
    public static bool IsAbsoluteWindowsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathFullyQualified(path); }
        catch { return false; }
    }
}
