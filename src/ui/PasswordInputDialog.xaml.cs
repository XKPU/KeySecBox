using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class PasswordInputDialog : ContentDialog
{
    public string Answer => PasswordBox.Password;

    public PasswordInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DialogAnim.Play(this);
    }

    internal void Init(string prompt)
    {
        PromptText.Text = prompt;
    }
}