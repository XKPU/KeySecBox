using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class InputDialog : ContentDialog
{
    public string Answer => AnswerBox.Text;

    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(string prompt, string initial = "")
    {
        PromptText.Text = prompt;
        AnswerBox.Text = initial;
        AnswerBox.SelectAll();
    }
}