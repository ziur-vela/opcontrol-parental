using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using OpControl.Parental.Client;

if (args.Contains("--restore-dns", StringComparer.OrdinalIgnoreCase))
{
    using var factory = LoggerFactory.Create(builder => builder.AddConsole());
    new NetworkDnsConfigurator(factory.CreateLogger<NetworkDnsConfigurator>()).Restore();
    return;
}

Host.CreateDefaultBuilder(args)
    .UseWindowsService(options => options.ServiceName = "OpControlParental")
    .ConfigureLogging((context, logging) =>
    {
        ClientPaths.EnsureDataDirectory();
        logging.AddProvider(new FileLoggerProvider(ClientPaths.LogFile));
    })
    .ConfigureServices(services =>
    {
        services.AddHttpClient<EnrollmentClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpControlParentalClient/0.2");
        });
        services.AddHttpClient("device", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpControlParentalClient/0.2");
        });
        services.AddSingleton<ClientSettings>(_ => new ClientSettings { Value = ClientOptions.Load() });
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<DeviceStateStore>();
        services.AddSingleton<StateStore>();
        services.AddSingleton<LockRuntime>();
        services.AddSingleton<LockoutStore>();
        services.AddSingleton<TaskManagerPolicy>();
        services.AddSingleton<OverrideSync>();
        services.AddSingleton<DataSyncService>();
        services.AddSingleton<CategoryCache>();
        services.AddSingleton<NetworkDnsConfigurator>();
        services.AddSingleton<DnsProxy>();
        services.AddSingleton<AppInventoryService>();
        services.AddSingleton<AppBlockService>();
        services.AddSingleton<DnsFilterService>();
        services.AddSingleton<HelperSupervisor>();
        services.AddSingleton<RuntimeLoopService>();
        services.AddSingleton<PinPipeServer>();
        services.AddHostedService(sp => sp.GetRequiredService<HelperSupervisor>());
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PinPipeServer>());
        services.AddHostedService(sp => sp.GetRequiredService<DataSyncService>());
        services.AddHostedService(sp => sp.GetRequiredService<AppInventoryService>());
        services.AddHostedService(sp => sp.GetRequiredService<AppBlockService>());
        services.AddHostedService(sp => sp.GetRequiredService<DnsFilterService>());
        services.AddHostedService<Worker>();
    })
    .Build()
    .Run();
