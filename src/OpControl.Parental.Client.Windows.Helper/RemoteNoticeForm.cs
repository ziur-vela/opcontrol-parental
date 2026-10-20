using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OpControl.Parental.Helper;

/// <summary>
/// Truly transparent, click-through notice shown while a remote-view session with
/// notices enabled is active. A layered window is painted with a 32-bit premultiplied
/// alpha bitmap so there is no panel, border or black box behind the eye and text.
/// </summary>
internal sealed class RemoteNoticeForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopMost = 0x00000008;
    private const int UlwAlpha = 2;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int SpiGetClientAreaAnimation = 0x1042;

    private static readonly Color NoticeText = Color.FromArgb(227, 236, 232);   // #E3ECE8
    private static readonly Color Shadow = Color.FromArgb(190, 4, 21, 11);
    private static readonly Color Eye = Color.FromArgb(0, 200, 83);             // #00C853

    private readonly Font _font = Theme.SansSemi(12f);
    private readonly System.Windows.Forms.Timer _pulseTimer;
    private readonly bool _animate;
    private string _text = "";
    private double _phase;

    public RemoteNoticeForm(string text)
    {
        _text = text;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        _ = Handle; // create the window before reading DeviceDpi

        _animate = ClientAreaAnimationEnabled();
        _pulseTimer = new System.Windows.Forms.Timer { Interval = 60 };
        _pulseTimer.Tick += (_, _) =>
        {
            _phase += 2 * Math.PI * 0.06 / 2.6;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            Render();
        };
    }

    public void ShowNotice(string text)
    {
        var changed = !string.Equals(_text, text, StringComparison.Ordinal);
        _text = text;
        if (!Visible) Show();
        TopMost = true;
        if (changed) _phase = 0;
        Render();
        if (_animate && !_pulseTimer.Enabled) _pulseTimer.Start();
    }

    public void HideNotice()
    {
        _pulseTimer.Stop();
        if (Visible) Hide();
    }

    private void Render()
    {
        var dpi = DeviceDpi <= 0 ? 96 : DeviceDpi;
        var scale = dpi / 96f;
        var iconHeight = Math.Max(1, (int)Math.Round(20 * scale));
        var iconWidth = Math.Max(1, (int)Math.Round(26 * scale));
        var gap = Math.Max(1, (int)Math.Round(8 * scale));
        var shadowRadius = Math.Max(1, (int)Math.Round(scale));

        using var meterBitmap = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        meterBitmap.SetResolution(dpi, dpi);
        using var meter = Graphics.FromImage(meterBitmap);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        var measured = meter.MeasureString(_text, _font, new PointF(0, 0), format);
        var textWidth = Math.Max(1, (int)Math.Ceiling(measured.Width));
        var textHeight = Math.Max(1, (int)Math.Ceiling(_font.GetHeight(meter)));

        var width = iconWidth + gap + textWidth + shadowRadius * 2 + 2;
        var height = Math.Max(iconHeight, textHeight) + shadowRadius * 2 + 2;

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var eyeAlpha = _animate ? 0.70 + 0.30 * (0.5 + 0.5 * Math.Sin(_phase)) : 1.0;
            DrawEye(graphics, new Rectangle(shadowRadius, (height - iconHeight) / 2 + shadowRadius, iconWidth, iconHeight), eyeAlpha);

            var textX = shadowRadius + iconWidth + gap;
            var textY = (height - textHeight) / 2f;

            using (var shadowBrush = new SolidBrush(Shadow))
            {
                for (var dx = -shadowRadius; dx <= shadowRadius; dx++)
                {
                    for (var dy = -shadowRadius; dy <= shadowRadius; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        graphics.DrawString(_text, _font, shadowBrush, textX + dx, textY + dy, format);
                    }
                }
            }

            using (var textBrush = new SolidBrush(NoticeText))
                graphics.DrawString(_text, _font, textBrush, textX, textY, format);
        }

        var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var destination = new Point(area.Left + (area.Width - width) / 2, area.Top + (int)Math.Round(12 * scale));
        ApplyLayeredBitmap(bitmap, destination);
    }

    private static void DrawEye(Graphics graphics, Rectangle box, double alpha)
    {
        // Almond outline plus pupil, filled with Alternate so the pupil is a real hole.
        using var path = new GraphicsPath(FillMode.Alternate);
        var left = box.Left;
        var right = box.Right;
        var top = box.Top;
        var bottom = box.Bottom;
        var midY = box.Top + box.Height / 2f;
        var cx = box.Left + box.Width / 2f;
        path.StartFigure();
        path.AddBezier(left, midY, left + box.Width * 0.27f, top, right - box.Width * 0.27f, top, right, midY);
        path.AddBezier(right, midY, right - box.Width * 0.27f, bottom, left + box.Width * 0.27f, bottom, left, midY);
        path.CloseFigure();
        var pupilRadius = box.Height * 0.21f;
        path.AddEllipse(cx - pupilRadius, midY - pupilRadius, pupilRadius * 2, pupilRadius * 2);

        using var brush = new SolidBrush(Color.FromArgb((int)Math.Round(255 * alpha), Eye));
        graphics.FillPath(brush, path);
    }

    private void ApplyLayeredBitmap(Bitmap bitmap, Point destination)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memoryDc, hBitmap);
        try
        {
            var source = new Point(0, 0);
            var size = new Size(bitmap.Width, bitmap.Height);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(hBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool ClientAreaAnimationEnabled()
    {
        try
        {
            var enabled = 1;
            return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0) && enabled != 0;
        }
        catch { return true; }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExLayered;
            cp.ExStyle |= WsExTransparent;
            cp.ExStyle |= WsExNoActivate;
            cp.ExStyle |= WsExToolWindow;
            cp.ExStyle |= WsExTopMost;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pulseTimer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(int uAction, int uParam, ref int lpvParam, int fuWinIni);
}
