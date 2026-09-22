using System.Windows;
using System.Windows.Input;

namespace KeySecBox;

/// <summary>通用单行文本输入对话框。</summary>
public partial class InputDialog : ContentDialogBase
{
    public string Answer => AnswerBox.Text;

    public InputDialog()
    {
        InitializeComponent();
        CloseButtonText = "";        // 用自定义按钮
        AttachButtons(OkBtn, CancelBtn);
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(string prompt, string initial = "")
    {
        PromptText.Text = prompt;
        AnswerBox.Text = initial;
        AnswerBox.SelectAll();
        AnswerBox.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => HideDialog();

    private void AnswerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        DialogResult = true;
    }
}
