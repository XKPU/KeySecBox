// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

/// <summary>
/// 同一 XamlRoot 同时只允许一个 ContentDialog。这里集中登记当前显示的对话框，
/// 使自动锁定能先收起界面上的明文（查看详情、编辑条目等），再弹出解锁框。
/// </summary>
internal static class DialogHost
{
    private static ContentDialog? _active;

    public static bool IsOpen => _active is not null;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dlg)
    {
        _active = dlg;
        try
        {
            return await dlg.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_active, dlg)) _active = null;
        }
    }

    /// <summary>收起当前对话框并等待其关闭；超时后不再等待，交由调用方继续。</summary>
    public static async Task CloseAsync(int timeoutMs = 800)
    {
        var dlg = _active;
        if (dlg is null) return;
        try { dlg.Hide(); } catch { }
        var sw = Stopwatch.StartNew();
        while (IsOpen && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(15);
    }
}
