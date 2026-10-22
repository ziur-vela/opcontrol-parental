// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using OpControl.Parental.Updater;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();

var options = new UpdaterOptions();
builder.Configuration.GetSection("Updater").Bind(options);
options.Secret = builder.Configuration["UPDATER_SECRET"] ?? options.Secret;
options.DbPassword = builder.Configuration["POSTGRES_PASSWORD"] ?? options.DbPassword;
options.ComposeDir = builder.Configuration["STACK_DIR"] ?? options.ComposeDir;
options.ComposeFile = Path.Combine(options.ComposeDir, "docker-compose.yml");
options.Network = builder.Configuration["STACK_NETWORK"] ?? options.Network;
options.ProjectName = builder.Configuration["STACK_PROJECT"] ?? options.ProjectName;
options.WebContainer = builder.Configuration["WEB_CONTAINER"] ?? options.WebContainer;
options.WebUrl = builder.Configuration["WEB_URL"] ?? options.WebUrl;
options.DbHost = builder.Configuration["DB_HOST"] ?? options.DbHost;
options.DbUser = builder.Configuration["DB_USER"] ?? options.DbUser;
options.DbName = builder.Configuration["DB_NAME"] ?? options.DbName;
options.BackupsPath = builder.Configuration["BACKUPS_PATH"] ?? options.BackupsPath;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<UpdaterState>();
builder.Services.AddSingleton<UpdateRunner>();
builder.Services.AddHttpClient();

var app = builder.Build();

bool Authorized(HttpContext context)
{
    if (string.IsNullOrWhiteSpace(options.Secret)) return false;
    return context.Request.Headers.TryGetValue("X-Updater-Secret", out var value)
        && string.Equals(value.ToString(), options.Secret, StringComparison.Ordinal);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow, version = UpdaterVersion() }));

app.MapGet("/version", () => Results.Ok(new { version = UpdaterVersion() }));

static string UpdaterVersion()
{
    var value = typeof(UpdateRunner).Assembly.GetName().Version;
    return value is null ? "0.0.0" : $"{value.Major}.{value.Minor}.{value.Build}";
}

app.MapGet("/status", (HttpContext context, UpdateRunner runner) =>
    Authorized(context) ? Results.Ok(runner.Status()) : Results.Unauthorized());

app.MapPost("/update", (HttpContext context, UpdateRequest request, UpdateRunner runner) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    var (started, error) = runner.Start(request);
    return started ? Results.Accepted("/status", new { started = true }) : Results.Conflict(new { error });
});

app.MapPost("/restore", async (HttpContext context, RestoreRequest request, UpdateRunner runner) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    var (started, error) = await runner.RestoreAsync(request);
    return started ? Results.Accepted("/status", new { started = true }) : Results.Conflict(new { error });
});

app.Run();
