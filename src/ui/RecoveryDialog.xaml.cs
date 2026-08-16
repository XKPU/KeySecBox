using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace KeySecBox;

public sealed partial class RecoveryDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private long _entryId;

    public RecoveryDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
        // 关闭即清空输入框，不在控件上滞留刚粘贴的密钥
        Closed += (_, _) => NewKeyBox.Text = "";
    }

    internal void Init(NativeMethods.Store store, long entryId, string label)
    {
        _store = store;
        _entryId = entryId;
        SubText.Text = string.IsNullOrEmpty(label)
            ? "恢复密钥用于双重验证（2FA）无法通过时的备用登录。"
            : $"条目：{label}";
        Reload();
    }

    #region 密钥管理

    private void Reload()
    {
        if (_store == null) return;
        KeyList.ItemsSource = _store.GetRecovery(_entryId);
        NewKeyBox.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Apply(List<string> keys)
    {
        var rc = _store!.SetRecovery(_entryId, keys);
        if (rc != NativeMethods.KSBOX_OK)
        {
            ErrorText.Text = $"保存失败（错误码 {rc}）。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var svc = _store.Save();
        Reload();
        if (svc != NativeMethods.KSBOX_OK)
        {
            ErrorText.Text = $"恢复密钥已写入内存，但保存到磁盘失败（错误码 {svc}）。";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void AddKey()
    {
        if (_store == null) return;
        var text = NewKeyBox.Text.Trim();
        if (text.Length == 0) return;
        var keys = _store.GetRecovery(_entryId);
        if (keys.Contains(text))
        {
            ErrorText.Text = "该恢复密钥已存在。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        keys.Add(text);
        Apply(keys);
    }

    #endregion

    #region 事件

    private void AddKey_Click(object sender, RoutedEventArgs e) => AddKey();

    private void NewKeyBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            AddKey();
            e.Handled = true;
        }
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null) return;
        if (sender is Button b && b.Tag is string key)
        {
            var keys = _store.GetRecovery(_entryId);
            keys.Remove(key);
            Apply(keys);
        }
    }

    private async void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key)
        {
            var pkg = new DataPackage();
            pkg.SetText(key);
            Clipboard.SetContent(pkg);
            b.Content = "已复制";
            await System.Threading.Tasks.Task.Delay(1200);
            b.Content = "复制";
        }
    }

    #endregion
}
