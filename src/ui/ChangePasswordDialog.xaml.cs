using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class ChangePasswordDialog : ContentDialog
{
    private NativeMethods.Store? _store;
    private bool _busy;
    public bool Succeeded { get; private set; }

    public ChangePasswordDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
        PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; // 由异步流程决定关闭
            await RunChangeAsync();
        };
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(NativeMethods.Store store)
    {
        _store = store;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = NewBox.Password.Length >= 1 && ConfirmBox.Password.Length >= 1;
    }

    public async Task<bool> ChangeAsync()
    {
        if (_store is not { } store) return false;
        _busy = true;
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "";
        Busy.IsActive = true;

        // 重加密全部条目可能耗时，放后台线程执行
        var rc = await Task.Run(() => store.ChangePassword(NewBox.Password));

        _busy = false;
        Busy.IsActive = false;
        CloseButtonText = "关闭";
        IsPrimaryButtonEnabled = true;

        if (rc == NativeMethods.KSBOX_OK)
        {
            Succeeded = true;
            Hide();
            return true;
        }
        ErrorText.Text = $"修改密码失败（错误码 {rc}）。";
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private bool Validate()
    {
        if (NewBox.Password.Length == 0) { ErrorText.Text = "新密码不能为空。"; return false; }
        if (NewBox.Password != ConfirmBox.Password)
        {
            ErrorText.Text = "两次输入的密码不一致。";
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        return true;
    }

    private async Task RunChangeAsync()
    {
        if (_busy) return;
        ErrorText.Visibility = Visibility.Collapsed;
        if (!Validate()) return;
        await ChangeAsync();
    }
}