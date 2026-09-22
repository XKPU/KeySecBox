using System.Windows;
using System.Windows.Controls;

namespace KeySecBox;

/// <summary>
/// 通用消息对话框，替代散落的 <c>new ContentDialog { Title=…, Content=… }</c>。
/// WPF 没有 ContentDialog，这里继承自 <see cref="ContentDialogBase"/> 自绘一个。
/// </summary>
public partial class MessageDialog : ContentDialogBase
{
    public MessageDialog(string message, string title = "提示", string closeText = "确定")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        CloseButtonText = closeText;
        AttachButtons(null, OkButton);
    }

    /// <summary>初始不会自动聚焦；显示后把焦点给确定按钮，Enter/Esc 均可关闭。</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        OkButton.Focus();
    }
}
