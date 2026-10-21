using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

public sealed class ChildMessageForm : Form
{
    private readonly ChildMessagingPanel _panel;

    public ChildMessageForm(bool aboveLock)
    {
        Text = Loc.T("Client.Messages.WindowTitle");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = aboveLock;
        MinimumSize = new Size(420, 460);
        Size = new Size(600, 680);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(14);

        _panel = new ChildMessagingPanel { Dock = DockStyle.Fill };
        Controls.Add(_panel);
        Dpi.ScaleForm(this);
        Shown += (_, _) => _panel.Start();
        FormClosed += (_, _) => _panel.Stop();
    }
}
