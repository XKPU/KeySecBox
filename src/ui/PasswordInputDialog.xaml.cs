// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KeySecBox;

public sealed partial class PasswordInputDialog : ContentDialog
{
    public string Answer => PasswordBox.Password;

    public PasswordInputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DialogAnim.Play(this);
            _ = PasswordBox.Focus(FocusState.Programmatic);
        };
        PasswordBox.PasswordChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
    }

    internal void Init(string prompt)
    {
        PromptText.Text = prompt;
    }

    // 校验失败时在同一对话框内提示，避免关掉重开
    internal void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // 取走 Answer 后立即擦除明文
    internal void ClearSecret() => PasswordBox.Password = "";
}
