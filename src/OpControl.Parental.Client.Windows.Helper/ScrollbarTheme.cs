// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpControl.Parental.Helper;

/// <summary>
/// Gives native scroll bars the window theme instead of the default light ones.
/// Windows 10 1809+ and Windows 11 provide a dark scroll bar style through the
/// "DarkMode_Explorer" visual style; "Explorer" is used for the light theme.
/// Attach once per form: every scrollable child, including ones added later
/// (views rebuild their controls), is themed when its handle is created.
/// </summary>
internal static class ScrollbarTheme
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    private static readonly ConditionalWeakTable<Control, object> Hooked = new();
    private static readonly bool Supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public static void Attach(Control root)
    {
        if (!Supported) return;
        Hook(root);
    }

    /// <summary>Re-applies the current theme to a control tree (after a light/dark switch).</summary>
    public static void Reapply(Control root)
    {
        if (!Supported) return;
        if (NeedsTheme(root) && root.IsHandleCreated) Apply(root);
        foreach (Control child in root.Controls) Reapply(child);
    }

    private static void Hook(Control control)
    {
        if (Hooked.TryGetValue(control, out _)) return;
        Hooked.Add(control, new object());

        if (NeedsTheme(control))
        {
            if (control.IsHandleCreated) Apply(control);
            control.HandleCreated += (_, _) => Apply(control);
        }
        control.ControlAdded += (_, e) =>
        {
            if (e.Control is not null) Hook(e.Control);
        };
        foreach (Control child in control.Controls) Hook(child);
    }

    private static bool NeedsTheme(Control c) =>
        c is not Form
        && c is ScrollableControl or TextBoxBase or ListBox or ListView or TreeView or DataGridView;

    private static void Apply(Control control)
    {
        try
        {
            SetWindowTheme(control.Handle, Theme.IsLight ? "Explorer" : "DarkMode_Explorer", null);
        }
        catch
        {
            // Older Windows or a control without a native window: keep the default scroll bars.
        }
    }
}
