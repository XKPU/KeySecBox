using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KeySecBox;

internal static class DialogAnim
{
    // 统一入场动画：淡入 + 自下而上轻微位移 + 略缩放到原大
    public static void Play(UIElement target)
    {
        var ct = new CompositeTransform { TranslateY = 14, ScaleX = 0.96, ScaleY = 0.96 };
        target.RenderTransform = ct;
        target.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);

        var sb = new Storyboard();

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.3) };
        fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        var slide = new DoubleAnimation { From = 14, To = 0, Duration = TimeSpan.FromSeconds(0.3) };
        slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(slide, target);
        Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        sb.Children.Add(slide);

        var scaleX = new DoubleAnimation { From = 0.96, To = 1.0, Duration = TimeSpan.FromSeconds(0.3) };
        scaleX.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(scaleX, target);
        Storyboard.SetTargetProperty(scaleX, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)");
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation { From = 0.96, To = 1.0, Duration = TimeSpan.FromSeconds(0.3) };
        scaleY.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        Storyboard.SetTarget(scaleY, target);
        Storyboard.SetTargetProperty(scaleY, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)");
        sb.Children.Add(scaleY);

        sb.Begin();
    }
}
