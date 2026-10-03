// SPDX-FileCopyrightText: 2026 K_PU
// SPDX-License-Identifier: AGPL-3.0-or-later
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