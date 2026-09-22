using System.Windows;
using System.Windows.Controls;

namespace KeySecBox;

/// <summary>要求用户输入一段密码文本（如旧版库主密码 / 加密导出文件密码）。</summary>
public partial class PasswordInputDialog : ContentDialogBase
{
    public string Answer => PasswordBox.Password;

    public PasswordInputDialog()
    {
        InitializeComponent();
        CloseButtonText = "";          // 用自定义「取消」按钮
        AttachButtons(OkBtn, CancelBtn);

        Loaded += (_, _) =>
        {
            DialogAnim.Play(this);
            // 原 XAML 的 DefaultButton="Primary"：显示后聚焦密码框，Enter 即确认
            PasswordBox.Focus();
        };
        // 关闭即清空，不留明文
        Closed += (_, _) => PasswordBox.Password = "";
    }

    internal void Init(string prompt)
    {
        PromptText.Text = prompt;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideDialog();
}
