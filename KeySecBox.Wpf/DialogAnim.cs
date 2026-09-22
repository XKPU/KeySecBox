using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KeySecBox;

/// <summary>
/// 统一入场动画（WPF 版）。
///
/// 与 WinUI 版的差异：WinUI 用 <c>CompositeTransform</c>，WPF 没有该类，
/// 改用 <c>TransformGroup</c>（TranslateTransform + ScaleTransform）。
/// 另外 WPF 的 <c>Storyboard.SetTarget</c> 只能指向 <c>FrameworkElement</c>，
/// 不能指向 <c>TranslateTransform</c>，因此这里直接调用 <c>BeginAnimation</c>。
/// </summary>
internal static class DialogAnim
{
    /// <summary>统一入场动画。</summary>
    public static void Play(UIElement target)
        => Play(target, AppSettings.AlignMsToFrames(AppSettings.DialogAnimMs));

    public static void Play(UIElement target, long ms)
    {
        if (target is not FrameworkElement fe) return;

        var dur = TimeSpan.FromMilliseconds(ms);
        var group = EnsureTransform(fe, out var translate, out var scale);
        translate.X = 0;
        translate.Y = 14;
        scale.ScaleX = 0.96;
        scale.ScaleY = 0.96;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Fade(fe, 0, 1, dur, ease);
        Animate(translate, TranslateTransform.YProperty, 14, 0, dur, ease);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.96, 1.0, dur, ease);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.96, 1.0, dur, ease);
    }

    /// <summary>列表整体入场：只做淡入 + 上移，不缩放。</summary>
    public static void PlayFadeUp(UIElement target, long ms)
    {
        if (target is not FrameworkElement fe) return;

        var dur = TimeSpan.FromMilliseconds(ms);
        var group = EnsureTransform(fe, out var translate, out _);
        translate.X = 0;
        translate.Y = 24;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        Fade(fe, 0, 1, dur, ease);
        Animate(translate, TranslateTransform.YProperty, 24, 0, dur, ease);
    }

    // ---- 内部 ----

    /// <summary>
    /// 取得（或装配）一个可安全动画的 TransformGroup。
    /// 若元素已有模板/外部设置的 RenderTransform，则保留并叠加，避免覆盖。
    /// </summary>
    private static TransformGroup EnsureTransform(FrameworkElement fe,
        out TranslateTransform translate, out ScaleTransform scale)
    {
        if (fe.RenderTransform is TransformGroup g
            && g.Children.Count >= 2
            && g.Children[0] is TranslateTransform t
            && g.Children[1] is ScaleTransform s)
        {
            translate = t;
            scale = s;
            return g;
        }

        translate = new TranslateTransform();
        scale = new ScaleTransform();
        var group = new TransformGroup();

        // 保留已有变换（如模板里的），放到最前面
        if (fe.RenderTransform is { } existing && existing is not TransformGroup)
            group.Children.Add(existing);

        group.Children.Add(translate);
        group.Children.Add(scale);
        fe.RenderTransform = group;
        return group;
    }

    private static void Fade(FrameworkElement fe, double from, double to,
                             TimeSpan dur, IEasingFunction ease)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = dur,
            EasingFunction = ease,
        };
        fe.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private static void Animate(Animatable target, DependencyProperty prop,
                                double from, double to, TimeSpan dur, IEasingFunction ease)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = dur,
            EasingFunction = ease,
        };
        target.BeginAnimation(prop, anim);
    }
}
