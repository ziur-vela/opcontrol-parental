// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Drawing;
using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

public sealed class MessagePopupForm : Form
{
    private readonly ChildMessageDto _message;

    public event Action<ChildMessageDto>? Seen;
    public event Action<ChildMessageDto>? Acknowledged;
    public event Action? ReplyClicked;

    public MessagePopupForm(ChildMessageDto message)
    {
        _message = message;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(18, 16, 18, 16);
        Width = 460;
        Height = 286;
        MinimumSize = new Size(380, 220);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        head.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var eyebrow = new Label
        {
            Text = Loc.T("Client.Messages.PopupTitle"),
            Font = Theme.Mono(9f, FontStyle.Bold),
            ForeColor = Theme.Label,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        var sender = new Label
        {
            Text = MessageDisplay.Sender(message),
            Font = Theme.Sans(15f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var time = new Label
        {
            Text = MessageDisplay.Time(message),
            Font = Theme.Mono(9f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 10)
        };
        head.Controls.Add(eyebrow, 0, 0);
        head.Controls.Add(sender, 0, 1);
        head.Controls.Add(time, 0, 2);

        var body = new Label
        {
            Text = MessageDisplay.Body(message),
            Font = Theme.Sans(12.5f),
            ForeColor = Theme.Text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Theme.Panel,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 4, 0, 12)
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        var primary = new Button
        {
            Text = message.RequireAck ? Loc.T("Client.Messages.GotIt") : Loc.T("Client.Messages.Close"),
            AutoSize = true,
            MinimumSize = new Size(120, 42),
            Margin = new Padding(8, 0, 0, 0)
        };
        Theme.StyleButton(primary, primary: true);
        primary.Click += (_, _) =>
        {
            if (message.RequireAck) Acknowledged?.Invoke(message);
            Close();
        };

        var reply = new Button
        {
            Text = Loc.T("Client.Messages.Reply"),
            AutoSize = true,
            MinimumSize = new Size(110, 42)
        };
        Theme.StyleButton(reply);
        reply.Click += (_, _) =>
        {
            ReplyClicked?.Invoke();
            Close();
        };

        buttons.Controls.Add(primary);
        buttons.Controls.Add(reply);

        layout.Controls.Add(head, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line2, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var accent = new Pen(Theme.Brand, 2);
            e.Graphics.DrawLine(accent, 0, 0, Width, 0);
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        Dpi.ScaleForm(this);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW, but still accepts input
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Seen?.Invoke(_message);
        if (_message.SpeakAloud)
            Speech.Speak(MessageDisplay.Body(_message), Loc.Language);
    }
}
