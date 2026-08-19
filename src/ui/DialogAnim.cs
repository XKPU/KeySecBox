using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox;

internal static class DialogAnim
{
    // 统一入场动画
    public static void Play(UIElement target)
        => Play(target, AppSettings.AlignMsToFrames(AppSettings.DialogAnimMs));

    public static void Play(UIElement target, long ms)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var ct = new CompositeTransform { TranslateY = 14, ScaleX = 0.96, ScaleY = 0.96 };
        target.RenderTransform = ct;
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        var sb = new Storyboard();

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = dur };
        fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        var slide = new DoubleAnimation { From = 14, To = 0, Duration = dur };
        slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(slide, target);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(slide);

        var scaleX = new DoubleAnimation { From = 0.96, To = 1.0, Duration = dur };
        scaleX.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(scaleX, target);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation { From = 0.96, To = 1.0, Duration = dur };
        scaleY.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(scaleY, target);
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
        sb.Children.Add(scaleY);

        sb.Begin();
    }

    // 列表整体入场
    public static void PlayFadeUp(UIElement target, long ms)
    {
        var dur = TimeSpan.FromMilliseconds(ms);
        var ct = new CompositeTransform { TranslateY = 24 };
        target.RenderTransform = ct;
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        var sb = new Storyboard();

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = dur };
        fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        var slide = new DoubleAnimation { From = 24, To = 0, Duration = dur };
        slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(slide, target);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(slide);

        sb.Begin();
    }
}
