using System.Drawing.Text;
using System.Reflection;

namespace OpControl.Parental.Helper;

/// <summary>OpControl Style tokens for the Windows helper. Fonts are embedded (OFL).</summary>
internal static class Theme
{
    public static Color Bg = Color.FromArgb(11, 23, 20);
    public static Color Panel = Color.FromArgb(14, 29, 25);
    public static Color Panel2 = Color.FromArgb(17, 36, 32);
    public static Color Line = Color.FromArgb(31, 58, 51);
    public static Color Line2 = Color.FromArgb(42, 74, 66);
    public static Color Text = Color.FromArgb(227, 236, 232);
    public static Color Muted = Color.FromArgb(127, 154, 146);
    public static Color Faint = Color.FromArgb(98, 122, 114);
    public static Color Label = Color.FromArgb(226, 133, 107);
    public static Color Value = Color.FromArgb(60, 201, 167);
    public static Color Brand = Color.FromArgb(0, 200, 83);
    public static Color BrandDark = Color.FromArgb(4, 21, 11);
    public static Color Danger = Color.FromArgb(229, 96, 79);
    public static Color Warn = Color.FromArgb(231, 176, 74);
    public static Color OwnBubble = Color.FromArgb(19, 48, 42);
    public static Color ParentBubble = Color.FromArgb(17, 36, 32);
    public static Color Highlight = Color.FromArgb(27, 58, 50);
    public static bool IsLight { get; private set; }

    public static void Apply(bool light)
    {
        IsLight = light;
        if (light)
        {
            Bg = Color.FromArgb(241, 244, 241);
            Panel = Color.FromArgb(255, 255, 255);
            Panel2 = Color.FromArgb(245, 248, 245);
            Line = Color.FromArgb(213, 222, 216);
            Line2 = Color.FromArgb(188, 203, 194);
            Text = Color.FromArgb(18, 32, 27);
            Muted = Color.FromArgb(76, 98, 89);
            Faint = Color.FromArgb(104, 125, 116);
            Label = Color.FromArgb(161, 63, 37);
            Value = Color.FromArgb(11, 107, 87);
            Brand = Color.FromArgb(0, 200, 83);
            BrandDark = Color.FromArgb(4, 21, 11);
            Danger = Color.FromArgb(178, 58, 45);
            Warn = Color.FromArgb(138, 97, 12);
            OwnBubble = Color.FromArgb(227, 244, 236);
            ParentBubble = Color.FromArgb(245, 248, 245);
            Highlight = Color.FromArgb(209, 238, 225);
        }
        else
        {
            Bg = Color.FromArgb(11, 23, 20);
            Panel = Color.FromArgb(14, 29, 25);
            Panel2 = Color.FromArgb(17, 36, 32);
            Line = Color.FromArgb(31, 58, 51);
            Line2 = Color.FromArgb(42, 74, 66);
            Text = Color.FromArgb(227, 236, 232);
            Muted = Color.FromArgb(127, 154, 146);
            Faint = Color.FromArgb(98, 122, 114);
            Label = Color.FromArgb(226, 133, 107);
            Value = Color.FromArgb(60, 201, 167);
            Brand = Color.FromArgb(0, 200, 83);
            BrandDark = Color.FromArgb(4, 21, 11);
            Danger = Color.FromArgb(229, 96, 79);
            Warn = Color.FromArgb(231, 176, 74);
            OwnBubble = Color.FromArgb(19, 48, 42);
            ParentBubble = Color.FromArgb(17, 36, 32);
            Highlight = Color.FromArgb(27, 58, 50);
        }
    }

    private static readonly List<PrivateFontCollection> Collections = new();
    private static readonly FontFamily? MonoRegular;
    private static readonly FontFamily? MonoSemiBold;
    private static readonly FontFamily? SansRegular;
    private static readonly FontFamily? SansSemiBold;
    private static readonly FontFamily? SansBold;

    static Theme()
    {
        MonoRegular = Load("OpControl.Parental.Helper.fonts.IBMPlexMono-Regular.ttf");
        MonoSemiBold = Load("OpControl.Parental.Helper.fonts.IBMPlexMono-SemiBold.ttf");
        SansRegular = Load("OpControl.Parental.Helper.fonts.Manrope-500.ttf");
        SansSemiBold = Load("OpControl.Parental.Helper.fonts.Manrope-600.ttf");
        SansBold = Load("OpControl.Parental.Helper.fonts.Manrope-700.ttf");
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

    public static Font SansSemi(float size) =>
        Make(SansSemiBold, "Segoe UI Semibold", size);

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

    public static void StyleForm(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = Sans(9.5f);
    }

    public static void StyleLabel(Label label, bool labelStyle = false)
    {
        label.ForeColor = labelStyle ? Label : Text;
        label.Font = labelStyle ? Mono(9f) : Sans(9.5f);
        label.BackColor = Color.Transparent;
    }

    public static void StyleInput(Control control)
    {
        control.BackColor = Panel2;
        control.ForeColor = Text;
        control.Font = Sans(12f);
    }

    public static void StyleButton(Button button, bool primary = false, bool danger = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = danger ? Danger : primary ? Brand : Line2;
        button.BackColor = primary ? Brand : Panel2;
        button.ForeColor = primary ? BrandDark : danger ? Danger : Text;
        button.Font = Mono(9.5f, FontStyle.Bold);
        button.Height = Math.Max(button.Height, 38);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }
}
