using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace KeySecBox;

public sealed partial class MainWindow
{
    private async void AddEntryBtn_Click(object sender, RoutedEventArgs e)
    {
        // 在某个分类下新增时预选该分类；全部视图不预选（保存时归入未分类）
        var dlg = new EntryDialog(this, _vault, null, _allScope ? null : _selectedCategory?.Id);
        ThemeDialog(dlg);
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            long rc = _vault.AddEntry(dlg.CategoryIds, dlg.Account, dlg.Password, dlg.Note);
            var recovery = dlg.Recovery;
            dlg.ClearSecrets(); // 取走数据后立即擦除明文
            if (rc > 0)
            {
                _vault.SetRecovery(rc, recovery);
                var svc = _vault.Save();
                RefreshCategories(); // 对话框内可能新建了分类
                RefreshEntriesWithIntro(); // 新条目左侧淡入
                if (svc != ErrorCodes.Ok)
                    await ShowError($"条目已加入内存，但保存失败（错误码 {svc}），重启后可能丢失。");
            }
            else await ShowError($"新增失败（错误码 {-rc}）。");
        }
    }

    private void EntryList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => EntryQueryBtn_Click(sender, e);

    // 条目排序：上移/下移即时生效。
    // 动画优先，动画完成后再写文件，避免 I/O 阻塞。
    private async void EntryMoveUpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not EntryItem row) return;
        int i = IndexOfId(_entryItems, row.Id, x => x.Id);
        if (i <= 0) return;
        await MoveEntryAsync(row, i - 1);
    }

    private async void EntryMoveDownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not EntryItem row) return;
        int i = IndexOfId(_entryItems, row.Id, x => x.Id);
        if (i < 0 || i >= _entryItems.Count - 1) return;
        await MoveEntryAsync(row, i + 1);
    }

    private Task MoveEntryAsync(EntryItem row, int to)
    {
        var oldTops = CaptureEntryTops();
        // 先就地移动 + 动画，动画完成后才写文件
        MoveEntryLocal(row.Id, to);
        // 就地移动后立即快照新顺序：动画期间列表可能被其它操作改变，
        // 若等到回调再读取，写回的顺序就不是用户刚看到的那一版。
        var newOrder = _entryItems.Select(e => e.Id).ToList();
        AnimateEntryMove(oldTops, async () =>
        {
            ErrorCodes rc;
            if (_allScope)
            {
                // 「全部」视图整体保存顺序，避免只 pin 单条导致其余条目回跳
                rc = _vault.SetAllOrder(newOrder);
            }
            else if (_selectedCategory is { } cat)
            {
                rc = _vault.MoveEntry(row.Id, cat.Id, to);
            }
            else
            {
                rc = ErrorCodes.Ok;
            }

            if (rc != ErrorCodes.Ok)
            {
                await ShowError($"排序失败（错误码 {rc}）。");
                return;
            }
            var svc = _vault.Save();
            if (svc != ErrorCodes.Ok)
                await ShowError($"排序已生效，但保存失败（错误码 {svc}），重启后可能丢失。");
        });
        return Task.CompletedTask;
    }

    // 把数据源中的一条就地移到新位置，并只刷新受影响行的上/下移箭头可用性
    private void MoveEntryLocal(long id, int to)
    {
        int i = IndexOfId(_entryItems, id, e => e.Id);
        if (i < 0 || i >= _entryItems.Count) return;
        if (to < 0) to = 0;
        if (to >= _entryItems.Count) to = _entryItems.Count - 1;
        _entryItems.Move(i, to);
        int first = System.Math.Min(i, to), last = System.Math.Max(i, to);
        for (int k = first; k <= last; k++)
        {
            var e = _entryItems[k];
            e.CanMoveUp = k > 0;
            e.CanMoveDown = k < _entryItems.Count - 1;
        }
    }

    private EntryItem? TryGetRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is EntryItem row) return row;
        return EntryList.SelectedItem as EntryItem;
    }

    // 详情模态窗：TextBlock 禁选防划词复制，右侧提供复制按钮
    private async void EntryQueryBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var full = _vault.GetEntry(row.Id);
        if (full == null) { await ShowError("读取条目失败。"); return; }

        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = row.NoteDisplay,
            Content = new StackPanel
            {
                Spacing = 10,
                Children = {
                    MakeField("账号", full.Account, allowCopy: true),
                    MakeField("密码", full.Password, allowCopy: true),
                    MakeField("备注", full.Note, allowCopy: true, selectable: true)
                }
            },
            CloseButtonText = "关闭"
        };
        ThemeDialog(dlg);
        // 关闭后卸载内容子树并置空明文字段，供 GC 回收
        EntryDetail entry = full;
        dlg.Closed += (_, _) =>
        {
            entry.Password = "";
            entry.Account = "";
            entry.Note = "";
            dlg.Content = new Grid();
        };
        await dlg.ShowAsync();
    }

    private async void EntryRecoveryBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var dlg = new RecoveryDialog(this, _vault, row.Id, row.NoteDisplay);
        ThemeDialog(dlg);
        await dlg.ShowAsync();
    }

    private async void EntryEditBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var full = _vault.GetEntry(row.Id);
        if (full == null) { await ShowError("读取条目失败。"); return; }

        var dlg = new EntryDialog(this, _vault, full);
        ThemeDialog(dlg);
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var rc = _vault.UpdateEntry(row.Id, dlg.CategoryIds, dlg.Account, dlg.Password, dlg.Note);
            var recovery = dlg.Recovery;
            dlg.ClearSecrets(); // 取走数据后立即擦除明文
            if (rc == ErrorCodes.Ok)
            {
                _vault.SetRecovery(row.Id, recovery);
                var svc = _vault.Save();
                RefreshCategories(); // 对话框内可能新建了分类
                RefreshEntries();
                if (svc != ErrorCodes.Ok)
                    await ShowError($"条目已更新，但保存失败（错误码 {svc}），重启后可能丢失。");
            }
            else await ShowError($"保存失败（错误码 {rc}）。");
        }
    }

    private async void EntryDelBtn_Click(object sender, RoutedEventArgs e)
    {
        var row = TryGetRow(sender, e);
        if (row == null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "删除条目",
            Content = $"确定删除「{row.NoteDisplay}」这条记录吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        ThemeDialog(dlg);
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var rc = _vault.RemoveEntry(row.Id);
            if (rc == ErrorCodes.Ok) { _vault.Save(); RefreshEntries(); }
            else await ShowError($"删除失败（错误码 {rc}）。");
        }
    }

    // 只读字段：默认禁选防划词复制（账号/密码），可选右侧复制按钮。
    private StackPanel MakeField(string label, string value, bool allowCopy, bool selectable = false)
    {
        var valueText = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "(空)" : value,
            IsTextSelectionEnabled = selectable,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        if (allowCopy)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(valueText, 0);
            var copy = new Button { Content = "复制", VerticalAlignment = VerticalAlignment.Top };
            string textToCopy = value;
            copy.Click += async (_, _) =>
            {
                var pkg = new DataPackage();
                pkg.SetText(textToCopy);
                Clipboard.SetContent(pkg);
                copy.Content = "已复制";
                await Task.Delay(1200);
                copy.Content = "复制";
            };
            Grid.SetColumn(copy, 1);
            row.Children.Add(valueText);
            row.Children.Add(copy);
            field.Children.Add(row);
        }
        else
        {
            field.Children.Add(valueText);
        }
        return field;
    }
}
