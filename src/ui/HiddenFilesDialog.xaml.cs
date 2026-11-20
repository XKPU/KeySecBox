// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace KeySecBox;

/// <summary>
/// 隐藏文件管理：列表来自加密保险库，隐藏状态实时取自磁盘属性。
/// 设置了单独密码的条目在「隐藏 / 恢复 / 移除」前需再校验一次。
/// </summary>
public sealed partial class HiddenFilesDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private IntPtr _ownerHwnd;

    /// <summary>行视图模型：静态快照，操作后整体刷新。</summary>
    internal sealed class HiddenRow
    {
        public HiddenRow(NativeMethods.HiddenItem item, bool hidden, bool missing)
        {
            Item = item;
            Hidden = hidden;
            Missing = missing;
        }

        public NativeMethods.HiddenItem Item { get; }
        public bool Hidden { get; }
        public bool Missing { get; }

        public string DisplayName => Item.DisplayName;
        public string FullPath => Item.Path;
        public bool HasPassword => Item.HasPassword;
        public string ToggleText => Hidden ? "恢复" : "隐藏";
        public string StateText => Missing ? "路径不可用" : Hidden ? "已隐藏" : "未隐藏";
    }

    public HiddenFilesDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store, IntPtr ownerHwnd)
    {
        _store = store;
        _ownerHwnd = ownerHwnd;
        Refresh();
    }

    #region 列表

    private void Refresh()
    {
        if (_store is not { } store) return;
        var rows = new List<HiddenRow>();
        foreach (var item in store.HiddenList().OrderByDescending(x => x.Added))
        {
            bool exists = HiddenFileService.Exists(item.Path);
            rows.Add(new HiddenRow(item, HiddenFileService.IsHidden(item.Path), !exists));
        }
        ItemList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        ItemList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message, IEnumerable<string>? errors = null)
    {
        var list = errors?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        StatusText.Text = list.Count > 0 ? "失败：" + string.Join("；", list) : message;
    }

    private static HiddenRow? RowOf(object sender)
        => (sender as FrameworkElement)?.Tag as HiddenRow;

    #endregion

    #region 添加

    private async void AddFileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;

            int hidden = 0;
            var errors = new List<string>();
            foreach (var file in files)
                if (AddAndHide(store, file.Path, errors)) hidden++;
            SaveAndRefresh(store, errors);
            SetStatus(hidden > 0 ? $"已隐藏 {hidden} 项" : "", errors);
        }
        catch (Exception ex)
        {
            SetStatus("", new[] { ex.Message });
        }
    }

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            var errors = new List<string>();
            bool hidden = AddAndHide(store, folder.Path, errors);
            SaveAndRefresh(store, errors);
            SetStatus(hidden ? "已隐藏 1 项" : "", errors);
        }
        catch (Exception ex)
        {
            SetStatus("", new[] { ex.Message });
        }
    }

    // 先写记录再改属性；属性失败则回滚记录，避免出现"有记录但未隐藏"
    private static bool AddAndHide(NativeMethods.Store store, string path, List<string> errors)
    {
        string label = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(label)) label = path;

        int rc = store.HiddenAdd(path, label);
        if (rc == NativeMethods.KSBOX_ERR_DUP)
        {
            errors.Add($"{label}：已在列表中");
            return false;
        }
        if (rc != NativeMethods.KSBOX_OK)
        {
            errors.Add($"{label}：记录失败（{rc}）");
            return false;
        }

        try
        {
            HiddenFileService.Hide(path);
            return true;
        }
        catch (Exception ex)
        {
            store.HiddenRemove(path);
            errors.Add($"{label}：{ex.Message}");
            return false;
        }
    }

    private void SaveAndRefresh(NativeMethods.Store store, List<string> errors)
    {
        if (store.Save() != NativeMethods.KSBOX_OK)
            errors.Add("保存失败，重启后记录可能丢失");
        Refresh();
    }

    #endregion

    #region 行操作

    private async void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store || RowOf(sender) is not { } row) return;
        if (!await AuthorizeAsync(row)) return;

        try
        {
            if (row.Hidden)
            {
                HiddenFileService.Restore(row.FullPath);
                SetStatus($"已恢复显示：{row.DisplayName}");
            }
            else
            {
                HiddenFileService.Hide(row.FullPath);
                SetStatus($"已隐藏：{row.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            SetStatus("", new[] { ex.Message });
        }
        Refresh();
    }

    private async void RenameBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store || RowOf(sender) is not { } row) return;

        var dlg = new InputDialog();
        dlg.Init("设置展示名称（只影响本程序内的显示）：", row.Item.Name);
        dlg.XamlRoot = XamlRoot;
        ThemeDialog(dlg);
        if (await DialogHost.ShowAsync(dlg) != ContentDialogResult.Primary) return;

        int rc = store.HiddenRename(row.FullPath, dlg.Answer.Trim());
        if (rc != NativeMethods.KSBOX_OK)
        {
            SetStatus("", new[] { $"重命名失败（{rc}）" });
            return;
        }
        var errors = new List<string>();
        SaveAndRefresh(store, errors);
        SetStatus("已更新展示名称", errors);
    }

    private async void PasswordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store || RowOf(sender) is not { } row) return;

        // 已有单独密码：先验证，作为修改/清除的前置条件
        string oldPassword = "";
        if (row.HasPassword)
        {
            var verified = await PromptPasswordAsync($"「{row.DisplayName}」已设置单独密码，请先输入当前密码：",
                p => store.HiddenCheckPassword(row.FullPath, p) == NativeMethods.KSBOX_OK);
            if (verified is null) return;
            oldPassword = verified;
        }

        var box = new PasswordBox { PlaceholderText = "留空表示不设置单独密码" };
        var clear = new CheckBox { Content = "清除单独密码（之后可直接隐藏 / 恢复）" };
        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "单独密码只作用于该项的「隐藏 / 恢复 / 移除」：进入软件后还需再校验一次。\n忘记该密码将无法操作此项，只能删除 data 目录中的 vault.hidden 重建列表（文件属性仍需手动恢复）。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        panel.Children.Add(box);
        panel.Children.Add(clear);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "单独密码",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        ThemeDialog(dlg);
        clear.Checked += (_, _) => box.IsEnabled = false;
        clear.Unchecked += (_, _) => box.IsEnabled = true;
        if (await DialogHost.ShowAsync(dlg) != ContentDialogResult.Primary) return;

        string newPassword = clear.IsChecked == true ? "" : box.Password;
        box.Password = "";
        int rc = store.HiddenSetPassword(row.FullPath, oldPassword, newPassword);
        if (rc != NativeMethods.KSBOX_OK)
        {
            SetStatus("", new[] { rc == NativeMethods.KSBOX_ERR_WRONG_PASSWORD ? "当前密码错误" : $"保存失败（{rc}）" });
            return;
        }
        var errors = new List<string>();
        SaveAndRefresh(store, errors);
        SetStatus(string.IsNullOrEmpty(newPassword) ? "已清除单独密码" : "已设置单独密码", errors);
    }

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store || RowOf(sender) is not { } row) return;
        if (!await AuthorizeAsync(row)) return;

        // 先撤销隐藏属性再删记录，避免留下"文件仍隐藏但没有记录"的孤儿
        if (row.Hidden)
        {
            try { HiddenFileService.Restore(row.FullPath); }
            catch (Exception ex)
            {
                SetStatus("", new[] { $"恢复属性失败，已保留记录：{ex.Message}" });
                return;
            }
        }

        int rc = store.HiddenRemove(row.FullPath);
        if (rc != NativeMethods.KSBOX_OK)
        {
            SetStatus("", new[] { $"移除失败（{rc}）" });
            return;
        }
        var errors = new List<string>();
        SaveAndRefresh(store, errors);
        SetStatus($"已从列表移除：{row.DisplayName}", errors);
    }

    #endregion

    #region 单独密码

    private async Task<bool> AuthorizeAsync(HiddenRow row)
    {
        if (_store is not { } store || !row.HasPassword) return true;
        var verified = await PromptPasswordAsync($"「{row.DisplayName}」已设置单独密码，请输入：",
            p => store.HiddenCheckPassword(row.FullPath, p) == NativeMethods.KSBOX_OK);
        return verified is not null;
    }

    // 返回校验通过的密码；取消返回 null。verify 为空表示不校验（仅取值）。
    private async Task<string?> PromptPasswordAsync(string prompt, Func<string, bool>? verify = null)
    {
        var dlg = new PasswordInputDialog();
        dlg.Init(prompt);
        dlg.XamlRoot = XamlRoot;
        ThemeDialog(dlg);
        while (true)
        {
            var res = await DialogHost.ShowAsync(dlg);
            if (res != ContentDialogResult.Primary)
            {
                dlg.ClearSecret();
                return null;
            }
            var pwd = dlg.Answer;
            dlg.ClearSecret();
            if (verify is null || verify(pwd)) return pwd;
            dlg.ShowError("密码错误，请重试。");
        }
    }

    #endregion

    private void ThemeDialog(ContentDialog dlg)
    {
        dlg.RequestedTheme = ActualTheme;
        dlg.CornerRadius = new CornerRadius(AppSettings.DialogCornerRadius);
    }
}
