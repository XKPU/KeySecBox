using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class SettingsDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private Action<ThemeMode>? _applyTheme;

    public SettingsDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnPrimaryButtonClick;
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store, Action<ThemeMode> applyTheme)
    {
        _store = store;
        _applyTheme = applyTheme;

        ThemePicker.SelectedIndex = AppSettings.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0
        };

        store.GetTombLimit(out uint maxBytes, out uint maxCount);
        MaxBytesBox.Value = Math.Max(1, maxBytes / (1024.0 * 1024.0));
        MaxCountBox.Value = maxCount;
        UpdateTombHint();
    }

    private void UpdateTombHint()
    {
        double mb = MaxBytesBox.Value;
        double cnt = MaxCountBox.Value;
        string size = mb >= 1 ? $"{mb:0.##} MB" : "不限";
        string count = cnt >= 1 ? $"{cnt:0} 条" : "不限";
        TombHint.Text = $"当前：{size} / {count}";
    }

    private void OnTombChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateTombHint();
    }

    private async void ChangePwdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_store is not { } store) return;
        var root = XamlRoot;   // 关闭设置前先捕获，供后续对话框使用
        var dlg = new ChangePasswordDialog();
        dlg.Init(store);
        dlg.XamlRoot = root;
        Hide();
        await dlg.ShowAsync();
        if (!dlg.Succeeded) return;
        var info = new ContentDialog
        {
            XamlRoot = root,
            Title = "KeySecBox",
            Content = "密码已修改，所有条目已用新密码重新加密。",
            CloseButtonText = "确定"
        };
        await info.ShowAsync();
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 校验通过后由异步流程关闭
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        // 主题
        var theme = ThemePicker.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        AppSettings.Theme = theme;
        _applyTheme?.Invoke(theme);

        // 墓碑上限
        if (_store is { } store)
        {
            uint bytes = (uint)(MaxBytesBox.Value * 1024.0 * 1024.0);
            uint count = (uint)MaxCountBox.Value;
            int rc = await Task.Run(() => store.SetTombLimit(bytes, count));
            if (rc != NativeMethods.KSBOX_OK)
            {
                StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                StatusText.Text = $"保存墓碑上限失败（错误码 {rc}）。";
                StatusText.Visibility = Visibility.Visible;
                return;
            }
        }

        StatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
        StatusText.Text = "设置已保存。";
        StatusText.Visibility = Visibility.Visible;
        Hide();
    }
}