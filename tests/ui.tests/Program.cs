// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// 前端纯逻辑测试：隐藏文件属性语义与 CSV 读写。
// 不依赖 WinUI，也不需要测试框架/测试宿主，直接以控制台程序运行并以退出码报告结果。
//
//   dotnet run --project tests/ui.tests -c Release

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KeySecBox.Tests;

internal static class Program
{
    private static int _checks;
    private static int _failures;
    private static string _current = "";

    private static void Fail(int line, string what)
    {
        _failures++;
        Console.WriteLine($"  FAIL [{_current}:{line}] {what}");
    }

    private static void Check(bool condition, int line, string expression)
    {
        _checks++;
        if (!condition) Fail(line, expression);
    }

    private static void Equal<T>(T expected, T actual, int line, string expression)
    {
        _checks++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Fail(line, $"{expression}: 期望 {expected}，实际 {actual}");
    }

    private static void SequenceEqual(IEnumerable<string> expected, IEnumerable<string> actual, int line, string expression)
    {
        _checks++;
        var e = expected.ToArray();
        var a = actual.ToArray();
        if (!e.SequenceEqual(a))
            Fail(line, $"{expression}: 期望 [{string.Join("|", e)}]，实际 [{string.Join("|", a)}]");
    }

    private static int Main()
    {
        var cases = new (string Name, Action Fn)[]
        {
            (nameof(Hide_sets_three_attributes_and_restore_clears_them), Hide_sets_three_attributes_and_restore_clears_them),
            (nameof(Restore_keeps_attributes_outside_the_three_flags), Restore_keeps_attributes_outside_the_three_flags),
            (nameof(Directories_are_supported), Directories_are_supported),
            (nameof(Missing_path_is_not_hidden), Missing_path_is_not_hidden),
            (nameof(Absolute_path_check), Absolute_path_check),
            (nameof(Parses_quoted_fields), Parses_quoted_fields),
            (nameof(Skips_trailing_empty_line), Skips_trailing_empty_line),
            (nameof(Escape_quotes_only_when_needed), Escape_quotes_only_when_needed),
            (nameof(Escape_and_parse_round_trip), Escape_and_parse_round_trip),
        };

        foreach (var (name, fn) in cases)
        {
            int before = _failures;
            _current = name;
            Console.WriteLine($"[ RUN  ] {name}");
            try
            {
                fn();
            }
            catch (Exception ex)
            {
                Fail(0, $"未捕获异常：{ex.GetType().Name}: {ex.Message}");
            }
            Console.WriteLine($"[ {(_failures == before ? " OK " : "FAIL")} ] {name}");
        }

        Console.WriteLine($"\n{_checks} 项断言，{_failures} 处失败");
        return _failures == 0 ? 0 : 1;
    }

    #region 隐藏文件属性

    // 测试沙箱放在程序目录下：某些受限环境（如无 TEMP 写权限）下系统临时目录不可写
    private static string NewTempDir()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ksbox_test_data");
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // 只读属性会让删除失败，先复位再清理
    private static void Cleanup(string dir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(dir, true);
        }
        catch { }
    }

    private static void Hide_sets_three_attributes_and_restore_clears_them()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "secret.txt");
            File.WriteAllText(path, "payload");
            var before = File.GetAttributes(path);

            Check(!HiddenFileService.IsHidden(path), 0, "新建文件不应处于隐藏状态");

            var captured = HiddenFileService.Hide(path);
            Equal(before, captured, 0, "Hide 返回的原始属性");

            var hidden = File.GetAttributes(path);
            Check(hidden.HasFlag(FileAttributes.Hidden), 0, "隐藏属性");
            Check(hidden.HasFlag(FileAttributes.System), 0, "系统属性");
            Check(hidden.HasFlag(FileAttributes.ReadOnly), 0, "只读属性");
            Check(HiddenFileService.IsHidden(path), 0, "IsHidden");

            HiddenFileService.Restore(path);

            var restored = File.GetAttributes(path);
            Check(!restored.HasFlag(FileAttributes.Hidden), 0, "恢复后无隐藏属性");
            Check(!restored.HasFlag(FileAttributes.System), 0, "恢复后无系统属性");
            Check(!restored.HasFlag(FileAttributes.ReadOnly), 0, "恢复后无只读属性");
            Equal(before, restored, 0, "恢复后属性应回到原值");
            Check(!HiddenFileService.IsHidden(path), 0, "恢复后 IsHidden 为 false");
        }
        finally { Cleanup(dir); }
    }

    private static void Restore_keeps_attributes_outside_the_three_flags()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "keep.txt");
            File.WriteAllText(path, "payload");
            File.SetAttributes(path, FileAttributes.Archive);

            HiddenFileService.Hide(path);
            HiddenFileService.Restore(path);

            // 文件系统可能自行附加 Compressed 等属性，只校验归档位被保留、三项隐藏位被清除
            var restored = File.GetAttributes(path);
            Check(restored.HasFlag(FileAttributes.Archive), 0, "归档属性应保留");
            Check((restored & HiddenFileService.HideFlags) == 0, 0, "三项隐藏属性应清除");
        }
        finally { Cleanup(dir); }
    }

    private static void Directories_are_supported()
    {
        var dir = NewTempDir();
        try
        {
            var folder = Path.Combine(dir, "folder");
            Directory.CreateDirectory(folder);

            HiddenFileService.Hide(folder);
            Check(HiddenFileService.IsHidden(folder), 0, "目录也可隐藏");

            HiddenFileService.Restore(folder);
            Check(!HiddenFileService.IsHidden(folder), 0, "目录可恢复");
        }
        finally { Cleanup(dir); }
    }

    private static void Missing_path_is_not_hidden()
    {
        var path = Path.Combine(Path.GetTempPath(), "ksbox_missing_" + Guid.NewGuid().ToString("N"));
        Check(!HiddenFileService.Exists(path), 0, "不存在的路径");
        Check(!HiddenFileService.IsHidden(path), 0, "不存在的路径不算隐藏");
        HiddenFileService.Restore(path); // 不应抛异常
    }

    private static void Absolute_path_check()
    {
        Check(HiddenFileService.IsAbsoluteWindowsPath(@"C:\Windows\notepad.exe"), 0, "C 盘绝对路径");
        Check(HiddenFileService.IsAbsoluteWindowsPath(@"\\server\share\file.txt"), 0, "UNC 路径");
        Check(!HiddenFileService.IsAbsoluteWindowsPath("relative\\file.txt"), 0, "相对路径");
        Check(!HiddenFileService.IsAbsoluteWindowsPath(""), 0, "空路径");
    }

    #endregion

    #region CSV

    private static void Parses_quoted_fields()
    {
        var rows = Csv.Parse("a,\"b,c\",\"d\"\"e\"\r\n\"multi\nline\",f,g");

        Equal(2, rows.Count, 0, "行数");
        SequenceEqual(new[] { "a", "b,c", "d\"e" }, rows[0], 0, "首行字段");
        SequenceEqual(new[] { "multi\nline", "f", "g" }, rows[1], 0, "次行字段");
    }

    private static void Skips_trailing_empty_line()
    {
        var rows = Csv.Parse("h1,h2\n1,2\n");
        Equal(2, rows.Count, 0, "末尾空行不应算作记录");
    }

    private static void Escape_quotes_only_when_needed()
    {
        Equal("plain", Csv.Escape("plain"), 0, "无需转义");
        Equal("\"a,b\"", Csv.Escape("a,b"), 0, "含逗号");
        Equal("\"a\"\"b\"", Csv.Escape("a\"b"), 0, "含引号");
        Equal("\"a\nb\"", Csv.Escape("a\nb"), 0, "含换行");
    }

    private static void Escape_and_parse_round_trip()
    {
        var fields = new[] { "账户", "p@ss,word", "含\"引号\"", "换\r\n行" };
        var line = string.Join(",", fields.Select(Csv.Escape));

        var rows = Csv.Parse(line);

        Equal(1, rows.Count, 0, "往返后行数");
        SequenceEqual(fields, rows[0], 0, "往返后字段");
    }

    #endregion
}
