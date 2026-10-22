// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Drawing.Text;
using System.Reflection;

namespace OpControl.Parental.Setup;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(11, 23, 20);
    public static readonly Color Panel = Color.FromArgb(14, 29, 25);
    public static readonly Color Panel2 = Color.FromArgb(17, 36, 32);
    public static readonly Color Line = Color.FromArgb(31, 58, 51);
    public static readonly Color Line2 = Color.FromArgb(42, 74, 66);
    public static readonly Color Text = Color.FromArgb(227, 236, 232);
    public static readonly Color Muted = Color.FromArgb(127, 154, 146);
    public static readonly Color Label = Color.FromArgb(226, 133, 107);
    public static readonly Color Value = Color.FromArgb(60, 201, 167);
    public static readonly Color Brand = Color.FromArgb(0, 200, 83);
    public static readonly Color BrandDark = Color.FromArgb(4, 21, 11);
    public static readonly Color Danger = Color.FromArgb(229, 96, 79);

    private static readonly List<PrivateFontCollection> Collections = new();
    private static readonly FontFamily? MonoRegular;
    private static readonly FontFamily? MonoSemiBold;
    private static readonly FontFamily? SansRegular;
    private static readonly FontFamily? SansSemiBold;
    private static readonly FontFamily? SansBold;

    static Theme()
    {
        MonoRegular = Load("OpControl.Parental.Setup.fonts.IBMPlexMono-Regular.ttf");
        MonoSemiBold = Load("OpControl.Parental.Setup.fonts.IBMPlexMono-SemiBold.ttf");
        SansRegular = Load("OpControl.Parental.Setup.fonts.Manrope-500.ttf");
        SansSemiBold = Load("OpControl.Parental.Setup.fonts.Manrope-600.ttf");
        SansBold = Load("OpControl.Parental.Setup.fonts.Manrope-700.ttf");
    }

    private static FontFamily? Load(string resource)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream is null) return null;
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");
            using (var file = File.Create(path)) stream.CopyTo(file);
            var collection = new PrivateFontCollection();
            collection.AddFontFile(path);
            Collections.Add(collection);
            try { File.Delete(path); } catch { }
            return collection.Families.FirstOrDefault();
        }
        catch { return null; }
    }

    public static Font Sans(float size, FontStyle style = FontStyle.Regular) =>
        Make(style.HasFlag(FontStyle.Bold) ? SansBold : SansRegular, "Segoe UI", size);

    public static Font Mono(float size, FontStyle style = FontStyle.Regular) =>
        Make(style.HasFlag(FontStyle.Bold) ? MonoSemiBold : MonoRegular, "Consolas", size);

    private static Font Make(FontFamily? family, string fallback, float size)
    {
        if (family is not null)
        {
            try { return new Font(family, size, FontStyle.Regular); } catch { }
        }
        return new Font(fallback, size, FontStyle.Regular);
    }

    public static void StyleForm(Form form) { form.BackColor = Bg; form.ForeColor = Text; form.Font = Sans(9.5f); }
    public static void StyleLabel(Label label, bool labelStyle = false) { label.ForeColor = labelStyle ? Label : Text; label.Font = labelStyle ? Mono(9f) : Sans(9.5f); label.BackColor = Color.Transparent; }
    public static void StyleInput(Control control) { control.BackColor = Panel2; control.ForeColor = Text; control.Font = Sans(11f); }
    public static void StyleButton(Button button, bool primary = false) { button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 1; button.FlatAppearance.BorderColor = primary ? Brand : Line2; button.BackColor = primary ? Brand : Panel2; button.ForeColor = primary ? BrandDark : Text; button.Font = Mono(9.5f, FontStyle.Bold); button.UseVisualStyleBackColor = false; button.Cursor = Cursors.Hand; }
}
