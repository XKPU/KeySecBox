using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KeySecBox;

/// <summary>
/// 自绘模态对话框基类，替代 WinUI 3 的 <c>ContentDialog</c>。
///
/// 为什么需要它：WPF 没有 ContentDialog。原 WinUI 版本每个对话框都是
/// <c>: ContentDialog</c> 并设置 <c>PrimaryButtonText</c> / <c>CloseButtonText</c>。
/// 这里用一个 Window 承担同样的角色：
///   - 遮罩父窗口、无边框、居中等同 ContentDialog 的视觉；
///   - 暴露同名属性（<see cref="PrimaryButtonText"/> / <see cref="CloseButtonText"/> /
///     <see cref="IsPrimaryButtonEnabled"/> / <see cref="Title"/>），
///     使原对话框的 XAML 与逻辑能尽量少改；
///   - 提供 <see cref="ShowDialogAsync"/>，对应原来的 <c>ShowAsync()</c>。
///
/// 派生类在 XAML 里把根节点写成 &lt;local:ContentDialogBase&gt; 并声明
/// Content / PrimaryButton / CloseButton 插槽即可。
/// </summary>
public class ContentDialogBase : Window
{
    // ---- 与 ContentDialog 对齐的属性 ----

    private string _primaryButtonText = "确定";
    private string _closeButtonText = "取消";
    private bool _isPrimaryButtonEnabled = true;

    public string PrimaryButtonText
    {
        get => _primaryButtonText;
        set { _primaryButtonText = value; SyncButtons(); }
    }

    public string CloseButtonText
    {
        get => _closeButtonText;
        set { _closeButtonText = value; SyncButtons(); }
    }

    public bool IsPrimaryButtonEnabled
    {
        get => _isPrimaryButtonEnabled;
        set { _isPrimaryButtonEnabled = value; SyncButtons(); }
    }

    /// <summary>点击「确定/主按钮」后是否允许关闭（原 args.Cancel 的等价物）。</summary>

    /// <summary>主按钮点击事件；处理函数中可设置 Cancel 阻止关闭。</summary>
    public event EventHandler<DialogButtonClickEventArgs>? PrimaryButtonClick;
    public event EventHandler? CloseButtonClick;

    // ---- 内部控件（由派生类 XAML 提供并命名） ----

    protected Button? PrimaryButtonControl;
    protected Button? CloseButtonControl;

    /// <summary>由派生类 XAML 通过 Loaded 调用，绑定按钮与标题。</summary>
    protected void AttachButtons(Button? primary, Button? close)
    {
        PrimaryButtonControl = primary;
        CloseButtonControl = close;

        if (PrimaryButtonControl != null)
            PrimaryButtonControl.Click += (_, _) => OnPrimaryClicked();
        if (CloseButtonControl != null)
            CloseButtonControl.Click += (_, _) =>
            {
                CloseButtonClick?.Invoke(this, EventArgs.Empty);
                Finish(false);
            };

        SyncButtons();
    }

    private void SyncButtons()
    {
        if (PrimaryButtonControl != null)
        {
            PrimaryButtonControl.Content = _primaryButtonText;
            PrimaryButtonControl.IsEnabled = _isPrimaryButtonEnabled;
        }
        if (CloseButtonControl != null)
        {
            CloseButtonControl.Content = _closeButtonText;
            // 空文本 = 不显示该按钮（对应 ContentDialog 里不设置 CloseButtonText）
            CloseButtonControl.Visibility = string.IsNullOrEmpty(_closeButtonText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnPrimaryClicked()
    {
        var args = new DialogButtonClickEventArgs();
        PrimaryButtonClick?.Invoke(this, args);
        if (args.Cancel) return;
        Finish(true);
    }

    /// <summary>对话框结果：主按钮=true，其余=false。</summary>
    public bool? Result { get; private set; }

    private void Finish(bool primary)
    {
        Result = primary;
        DialogResult = primary;   // 关闭模态窗口；ShowDialog 返回
    }

    // ---- 生命周期 ----

    protected ContentDialogBase()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // Esc = 关闭（等价 ContentDialog 的取消行为）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseButtonClick?.Invoke(this, EventArgs.Empty);
                Finish(false);
            }
        };
    }

    /// <summary>
    /// 以模态方式显示（对应原 <c>ShowAsync()</c>）。
    /// 必须传入 Owner，否则不会遮罩父窗口且可能跑到主窗口后面。
    /// </summary>
    public bool? ShowDialogAsync(Window owner)
    {
        if (owner != null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            // 覆盖父窗口客户区，形成模态遮罩
            Width = owner.ActualWidth;
            Height = owner.ActualHeight;
            Left = owner.Left;
            Top = owner.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        return ShowDialog();
    }

    /// <summary>关闭对话框并返回结果（对应原 <c>Hide()</c>）。</summary>
    public void HideDialog()
    {
        Result = null;
        DialogResult = false;
    }
}

/// <summary>主按钮点击参数：Cancel=true 表示阻止关闭（对齐 ContentDialogButtonClickEventArgs）。</summary>
public sealed class DialogButtonClickEventArgs : EventArgs
{
    public bool Cancel { get; set; }
}
