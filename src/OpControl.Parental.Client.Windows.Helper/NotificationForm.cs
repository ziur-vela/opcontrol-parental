using System.Drawing;
using System.Text.Json;

namespace OpControl.Parental.Helper;

public sealed class NotificationForm : Form
{
    private readonly System.Windows.Forms.Timer _timer;

    public NotificationForm(string title, string message)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Padding = new Padding(16, 12, 16, 12);
        Width = 360;
        Height = 96;

        var heading = new Label
        {
            Text = title,
            Font = Theme.Mono(9.5f, FontStyle.Bold),
            ForeColor = Theme.Label,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24
        };
        var body = new Label
        {
            Text = message,
            Font = Theme.Sans(9.5f),
            ForeColor = Theme.Text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft
        };
        Controls.Add(body);
        Controls.Add(heading);
        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line2, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        Dpi.ScaleForm(this);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);

        _timer = new System.Windows.Forms.Timer { Interval = 4500 };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    public static (string Title, string Message, DateTime Utc)? Read()
    {
        try
        {
            if (!File.Exists(HelperPaths.NotificationFile)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(HelperPaths.NotificationFile));
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var utc = root.TryGetProperty("utc", out var u) && u.TryGetDateTime(out var parsed) ? parsed : DateTime.MinValue;
            if (title.Length == 0 && message.Length == 0) return null;
            return (title, message, utc);
        }
        catch { return null; }
    }
}
