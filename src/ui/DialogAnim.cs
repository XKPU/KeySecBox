using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox;

internal static class DialogAnim
{
    public static void Play(UIElement target)
    {
        var sb = new Storyboard();
        var oa = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromSeconds(0.26)
        };
        oa.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(oa, target);
        Storyboard.SetTargetProperty(oa, "Opacity");
        sb.Children.Add(oa);
        sb.Begin();
    }
}
