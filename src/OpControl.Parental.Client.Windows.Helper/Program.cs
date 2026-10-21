using OpControl.Parental.Helper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--open-kid", StringComparison.OrdinalIgnoreCase)))
        {
            var tab = GetArgument(args, "--tab") ?? "home";
            var theme = GetArgument(args, "--theme");
            var language = GetArgument(args, "--language");
            var messagesOnly = args.Any(a => string.Equals(a, "--messages-only", StringComparison.OrdinalIgnoreCase));
            var response = PinPipeClient.OpenKidAsync(tab, theme, language, messagesOnly, CancellationToken.None)
                .GetAwaiter().GetResult();
            HelperPaths.LogEvent($"open-kid-command tab={tab} theme={theme} language={language} only={messagesOnly} ok={response.Ok} error={response.Error}");
            return;
        }

        var monitored = GetArgument(args, "--monitored");
        if (string.IsNullOrWhiteSpace(monitored))
        {
            HelperPaths.LogEvent("helper-missing-monitored-account");
            return;
        }
        if (monitored.Contains('\\')) monitored = monitored[(monitored.LastIndexOf('\\') + 1)..];
        if (!string.Equals(Environment.UserName, monitored, StringComparison.OrdinalIgnoreCase))
            return;

        using var mutex = new Mutex(true, @"Local\OpControl.Parental.Helper", out var createdNew);
        if (!createdNew) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new HelperContext(monitored));
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
