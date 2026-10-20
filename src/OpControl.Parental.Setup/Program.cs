using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Text.RegularExpressions;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var language = Arg(args, "LANG") ?? Arg(args, "--lang")
            ?? (System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? Loc.Spanish : Loc.English);
        Loc.SetLanguage(language);
        ApplicationConfiguration.Initialize();
        var silent = args.Any(a => a is "/S" or "/s" or "--silent" or "/quiet" or "/qn");
        var server = Arg(args, "SERVER") ?? Arg(args, "--server") ?? "https://parental.opcontrol.net";
        var code = Arg(args, "CODE") ?? Arg(args, "--code") ?? CodeFromFileName();
        var account = Arg(args, "ACCOUNT") ?? Arg(args, "--account");

        string msi;
        try
        {
            msi = ExtractMsi();
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(Loc.T("Setup.MissingPackage", ex.Message), "Parental OpControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        try
        {
            if (silent)
            {
                if (string.IsNullOrWhiteSpace(code)) return 2;
                if (string.IsNullOrWhiteSpace(account)) return 2;
                return RunMsi(msi, server, code, account, null);
            }

            using var form = new InstallForm(msi, server, code, account);
            return form.ShowDialog() == DialogResult.OK ? form.ExitCode : 1;
        }
        finally
        {
            try { File.Delete(msi); } catch { }
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1].Trim('"') : null;
            var prefix = name + "=";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..].Trim('"');
        }
        return null;
    }

    private static string? CodeFromFileName()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            var match = Regex.Match(name ?? "", @"OpControlParental-([A-Za-z0-9]{8,14})$", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
        }
        catch { return null; }
    }

    private static string ExtractMsi()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("OpControlParental.msi")
            ?? throw new InvalidOperationException("Embedded MSI not found.");
        var path = Path.Combine(Path.GetTempPath(), $"OpControlParental-{Guid.NewGuid():N}.msi");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    internal static int RunMsi(string msi, string server, string code, string account, Action<string>? progress)
    {
        progress?.Invoke(Loc.T("Setup.InstallingLabel"));
        var arguments = $"/i \"{msi}\" /qn /norestart SERVER=\"{server.TrimEnd('/')}\" CODE=\"{code.Trim()}\" ACCOUNT=\"{account.Trim()}\"";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return 4;
        process.WaitForExit();
        progress?.Invoke(process.ExitCode == 0 ? Loc.T("Setup.CompleteLabel") : Loc.T("Setup.FailedLabel", process.ExitCode));
        return process.ExitCode;
    }

    internal static List<string> LocalAccounts(out HashSet<string> administrators)
    {
        var accounts = new List<string>();
        administrators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var adminSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var groups = new ManagementObjectSearcher("SELECT * FROM Win32_Group WHERE LocalAccount=True"))
            {
                foreach (ManagementObject group in groups.Get())
                {
                    var sid = group["SID"]?.ToString() ?? "";
                    if (sid is "S-1-5-32-544" or "S-1-5-32-545") adminSids.Add($"{group["Domain"]}\\{group["Name"]}");
                }
            }

            using (var users = new ManagementObjectSearcher("SELECT * FROM Win32_UserAccount WHERE LocalAccount=True AND Disabled=False"))
            {
                foreach (ManagementObject user in users.Get())
                {
                    var name = $"{user["Domain"]}\\{user["Name"]}";
                    var sid = user["SID"]?.ToString() ?? "";
                    if (sid.EndsWith("-500") || sid.EndsWith("-501") || sid.EndsWith("-503")) continue;
                    accounts.Add(name);
                }
            }

            using (var members = new ManagementObjectSearcher("SELECT * FROM Win32_GroupUser"))
            {
                foreach (ManagementObject member in members.Get())
                {
                    var group = member["GroupComponent"]?.ToString() ?? "";
                    var part = member["PartComponent"]?.ToString() ?? "";
                    var match = Regex.Match(group, "Name=\"([^\"]+)\"");
                    var userMatch = Regex.Match(part, "Domain=\"([^\"]+)\",Name=\"([^\"]+)\"");
                    if (!match.Success || !userMatch.Success) continue;
                    if (!adminSids.Any(s => s.EndsWith("\\" + match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))) continue;
                    administrators.Add($"{userMatch.Groups[1].Value}\\{userMatch.Groups[2].Value}");
                }
            }
        }
        catch { }
        return accounts.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

internal sealed class InstallForm : Form
{
    private readonly string _msi;
    private readonly TextBox _server = new();
    private readonly TextBox _code = new();
    private readonly ComboBox _account = new();
    private readonly Label _status = new();
    private readonly Button _install = new();
    private readonly ProgressBar _progress = new();
    private readonly HashSet<string> _admins;

    public int ExitCode { get; private set; } = 1;

    public InstallForm(string msi, string server, string? code, string? account)
    {
        _msi = msi;
        Text = Loc.T("Setup.WindowTitle");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Theme.StyleForm(this);
        ClientSize = new Size(560, 404);
        Padding = new Padding(20);

        var title = new Label
        {
            Text = Loc.T("Setup.Title"),
            Font = Theme.Sans(18f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(22, 18)
        };
        var subtitle = new Label
        {
            Text = Loc.T("Setup.Subtitle"),
            AutoSize = false,
            Size = new Size(510, 54),
            Location = new Point(24, 58),
            ForeColor = Theme.Muted,
            Font = Theme.Mono(9f)
        };

        var serverLabel = new Label { Text = Loc.T("Setup.ServerLabel"), AutoSize = true, Location = new Point(24, 120), ForeColor = Theme.Label, Font = Theme.Mono(9f) };
        _server.Location = new Point(24, 140);
        _server.Size = new Size(510, 28);
        _server.Text = server;

        var codeLabel = new Label { Text = Loc.T("Setup.CodeLabel"), AutoSize = true, Location = new Point(24, 176), ForeColor = Theme.Label, Font = Theme.Mono(9f) };
        _code.Location = new Point(24, 196);
        _code.Size = new Size(240, 28);
        _code.Text = code ?? "";

        var accountLabel = new Label { Text = Loc.T("Setup.AccountLabel"), AutoSize = true, Location = new Point(24, 232), ForeColor = Theme.Label, Font = Theme.Mono(9f) };
        _account.Location = new Point(24, 252);
        _account.Size = new Size(510, 28);
        _account.DropDownStyle = ComboBoxStyle.DropDownList;

        var accounts = Program.LocalAccounts(out _admins);
        var ordered = accounts
            .OrderBy(a => _admins.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var name in ordered)
        {
            var isAdmin = _admins.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            _account.Items.Add(isAdmin ? $"{name} ({Loc.T("Setup.AdminSuffix")})" : name);
        }
        if (!string.IsNullOrWhiteSpace(account))
        {
            var index = ordered.FindIndex(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Split('\\').Last(), account, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _account.SelectedIndex = index;
        }

        _install.Enabled = _account.SelectedIndex >= 0;
        _account.SelectedIndexChanged += (_, _) => _install.Enabled = _account.SelectedIndex >= 0;

        _status.Location = new Point(24, 290);
        _status.Size = new Size(510, 24);
        _status.AutoSize = false;
        _status.ForeColor = Theme.Muted;
        _status.Font = Theme.Mono(9f);

        _progress.Location = new Point(24, 326);
        _progress.Size = new Size(510, 8);
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;

        _install.Text = Loc.T("Setup.Install");
        _install.Size = new Size(140, 34);
        _install.Location = new Point(394, 342);
        Theme.StyleInput(_server); Theme.StyleInput(_code); Theme.StyleInput(_account);
        Theme.StyleButton(_install, primary: true);
        _install.Click += async (_, _) => await InstallAsync();

        var cancel = new Button { Text = Loc.T("Setup.Cancel"), Size = new Size(100, 34), Location = new Point(284, 342), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(cancel);

        var foot = new Label { Text = Loc.T("Setup.Footer"), AutoSize = true, Location = new Point(24, 372), ForeColor = Theme.Muted, Font = Theme.Mono(8f) };
        Controls.AddRange([title, subtitle, serverLabel, _server, codeLabel, _code, accountLabel, _account, _status, _progress, _install, cancel, foot]);
        AcceptButton = _install;
        CancelButton = cancel;
        Shown += (_, _) => _install.Focus();
    }

    private async Task InstallAsync()
    {
        var server = _server.Text.Trim();
        var code = _code.Text.Trim();
        var account = (_account.SelectedItem?.ToString() ?? "").Replace($" ({Loc.T("Setup.AdminSuffix")})", "");
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            MessageBox.Show(Loc.T("Setup.InvalidUrl"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (code.Length < 6)
        {
            MessageBox.Show(Loc.T("Setup.EnterCode"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(account))
        {
            MessageBox.Show(Loc.T("Setup.ChooseAccount"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = false;
        _account.Enabled = false;
        _server.Enabled = false;
        _code.Enabled = false;
        _progress.Visible = true;
        _status.Text = Loc.T("Setup.StatusInstalling");
        var exit = await Task.Run(() => Program.RunMsi(_msi, server, code, account, text => BeginInvoke(() => _status.Text = text)));
        ExitCode = exit;
        _progress.Visible = false;
        if (exit == 0)
        {
            MessageBox.Show(Loc.T("Setup.Success"), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            MessageBox.Show(Loc.T("Setup.Failed", exit), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true;
            _account.Enabled = true;
            _server.Enabled = true;
            _code.Enabled = true;
        }
    }
}
