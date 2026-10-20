using System.Net.Http.Headers;
using System.Net.Http.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public static class ClientCommands
{
    public static async Task ConfigureAsync(string[] args)
    {
        ClientPaths.EnsureDirectoriesWithAcl();
        var options = ClientOptions.Load();
        var server = Arg(args, "--server") ?? Arg(args, "SERVER");
        var code = Arg(args, "--code") ?? Arg(args, "CODE");
        var account = Arg(args, "--account") ?? Arg(args, "ACCOUNT");
        if (!string.IsNullOrWhiteSpace(server)) options.ServerUrl = server.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(code)) options.EnrollmentCode = code.Trim();
        if (!string.IsNullOrWhiteSpace(account))
        {
            options.OsAccountName = account.Trim();
            options.MonitoredAccount = account.Trim();
        }
        if (string.IsNullOrWhiteSpace(options.HelperPath))
            options.HelperPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Helper", "OpControl.Parental.Helper.exe"));
        if (string.IsNullOrWhiteSpace(options.DeviceName)) options.DeviceName = Environment.MachineName;
        options.Save();
        try { SecureFile.SystemOnly(ClientPaths.ConfigFile); } catch { }
        Console.WriteLine("OpControl configuration saved.");
        await Task.CompletedTask;
    }

    public static async Task PrepareUninstallAsync()
    {
        ClientPaths.EnsureDataDirectory();
        try { await File.WriteAllTextAsync(ClientPaths.UninstallingFile, DateTime.UtcNow.ToString("o")); } catch { }
        try
        {
            var credential = new CredentialStore().Load();
            var options = ClientOptions.Load();
            if (credential is { IsUsable: true })
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ServerUrl.TrimEnd('/')}/api/device/uninstall")
                {
                    Content = JsonContent.Create(new UninstallReportRequest("Uninstall started on the device."))
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Credential);
                using var response = await client.SendAsync(request);
                Console.WriteLine($"Uninstall report: {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Uninstall report failed: " + ex.Message);
        }
    }

    public static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (string.Equals(value, name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            var prefix = name + "=";
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..].Trim('"');
        }
        return null;
    }

    public static string ClientVersion => typeof(ClientCommands).Assembly.GetName().Version?.ToString(3) ?? "0.7.3";
}
