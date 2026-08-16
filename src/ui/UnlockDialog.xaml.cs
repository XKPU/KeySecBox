using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class UnlockDialog : ContentDialog
{
    public string Password => PasswordBox.Password;
    public bool IsSetupMode { get; private set; }

    /// <summary>用户点击「忘记密码」后由流程标记并自行 Hide()（子对话框不能与父 ContentDialog 并存）。</summary>
    public bool ForgotHandled { get; set; }

    #region 初始化

    public UnlockDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    public UnlockDialog(Window owner, bool setup) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        IsSetupMode = setup;
        if (setup)
        {
            Title = MakeTitle("设置主密码");
            PrimaryButtonText = "创建保险库";
            HeadlineText.Text = "首次使用，请创建主密码";
            SubText.Text = "输入两次以确认。主密码将用于加密本地保险库，请务必牢记。";
        }
        else
        {
            Title = MakeTitle("解锁保险库");
            PrimaryButtonText = "解锁";
            HeadlineText.Text = "欢迎回来";
            SubText.Text = "请输入主密码以解密本地保险库。";
            ConfirmBox.Visibility = Visibility.Collapsed;
            if (RecoveryManager.GetConfig().Any) // 已配置恢复方式才展示入口
                ForgotPwdLink.Visibility = Visibility.Visible;
        }
    }

    // 原生图标 + 文本的对话框标题（替代 emoji）
    private static Grid MakeTitle(string text)
    {
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = "\uE72E", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(label, 1);
        title.Children.Add(icon);
        title.Children.Add(label);
        return title;
    }

    #endregion

    #region 事件

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        DialogAnim.Play(this); // 统一淡入动画
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        bool ok = !string.IsNullOrEmpty(PasswordBox.Password)
            && (IsSetupMode ? !string.IsNullOrEmpty(ConfirmBox.Password) : true);
        IsPrimaryButtonEnabled = ok;
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 设置模式下需两次输入一致；不一致则拦截默认关闭
        if (IsSetupMode && PasswordBox.Password != ConfirmBox.Password)
        {
            args.Cancel = true;
            ShowError("两次输入的主密码不一致，请重新输入。");
        }
    }

    private void ForgotPwdLink_Click(object sender, RoutedEventArgs e)
    {
        // 子对话框不能与父 ContentDialog 并存：先隐藏自身，由 MainWindow 显示恢复对话框
        ForgotHandled = true;
        Hide();
    }

    #endregion

    #region 辅助

    public void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
        ClearSecrets();
        IsPrimaryButtonEnabled = false;
    }

    // 取走 Password 后立刻调用：清空 PasswordBox，断开主密钥引用以便 GC 回收
    public void ClearSecrets()
    {
        PasswordBox.Password = "";
        ConfirmBox.Password = "";
    }

    #endregion
}
