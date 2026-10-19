using OpControl.Parental.Helper;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var monitored = GetArgument(args, "--monitored") ?? "Emma";
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
