using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly IAppConfigurationService _config;

    public SettingsDialog() { InitializeComponent(); _config = null!; }

    public SettingsDialog(Window owner, IAppConfigurationService config) : this()
    {
        XamlRoot = owner.Content.XamlRoot;
        _config = config;
        ThemeCombo.SelectedIndex = (int)config.Theme;
        FrameRateSlider.Value = config.FrameRate;

        PrimaryButtonClick += (_, _) =>
        {
            config.Theme = (ThemeMode)ThemeCombo.SelectedIndex;
            config.FrameRate = (int)FrameRateSlider.Value;
            config.Save();
        };
    }

    private void ContentDialog_Loaded(object sender, RoutedEventArgs e) => DialogAnim.Play(this);
}