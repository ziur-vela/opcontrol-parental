using Microsoft.Extensions.DependencyInjection;
using OpControl.Parental.Client;
using Microsoft.Extensions.Hosting.WindowsServices;

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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpControlParentalClient/0.1");
        });
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<StateStore>();
        services.AddHostedService<Worker>();
    })
    .Build()
    .Run();
