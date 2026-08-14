using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox;

public sealed partial class UnlockDialog : ContentDialog
{
    public string Password => PasswordBox.Password;
    public bool IsSetupMode { get; private set; }

    public UnlockDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    public UnlockDialog(Window owner, bool setup) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        IsSetupMode = setup;
        if (setup) Title = "🔐 设置解锁密码";
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e)
    {
        var sb = new Storyboard();
        var oa = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.3) };
        oa.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(oa, this);
        Storyboard.SetTargetProperty(oa, "Opacity");
        sb.Children.Add(oa);
        sb.Begin();
        _ = PasswordBox.Focus(FocusState.Programmatic);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(PasswordBox.Password);
    }

    public void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
        PasswordBox.Password = "";
        IsPrimaryButtonEnabled = false;
    }
}
