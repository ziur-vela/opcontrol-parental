using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Auth;
using OpControl.Parental.Server.Components;
using OpControl.Parental.Server.Data;
using OpControl.Parental.Server.Hubs;
using OpControl.Parental.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Database=parental;Username=parental;Password=parental";

builder.Services.AddDbContext<ParentalDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(5)));

builder.Services
    .AddIdentity<ParentUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 10;
    })
    .AddEntityFrameworkStores<ParentalDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<ParentUser>, ParentUserClaimsPrincipalFactory>();
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(DeviceTokenAuthenticationHandler.SchemeName, null);

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.Cookie.Name = "ocp.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorPages();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.EnableDetailedErrors = true;
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
    options.Cookie.Name = "ocp.af";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("enroll", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var dataProtection = builder.Services.AddDataProtection();
var dataProtectionPath = builder.Configuration["DataProtection:Path"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<FamilyScope>();
builder.Services.AddScoped<DeviceAuthService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<InstallService>();
builder.Services.AddScoped<UpdateService>();
builder.Services.AddScoped<LockService>();
builder.Services.AddScoped<RuleService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DashboardEventBus>();
builder.Services.AddSingleton<ReleaseCatalog>();
builder.Services.AddSingleton<DevicePresenceService>();
builder.Services.AddHostedService<LockExpiryService>();
builder.Services.AddHostedService<CategoryRefreshService>();
builder.Services.AddHostedService<ScheduleTransitionService>();
builder.Services.AddHostedService<UsageLimitService>();

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Configuration.GetValue("Database:Migrate", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 30)
        {
            logger.LogWarning(ex, "Database migration attempt {Attempt} failed; retrying", attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapRazorPages();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<ParentalHub>("/hubs/device");

app.MapGet("/health", async (ParentalDbContext db, CancellationToken ct) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        return Results.Ok(new { status = "ok", database = "ok", utc = DateTime.UtcNow });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "degraded", database = "error", utc = DateTime.UtcNow, detail = ex.GetType().Name }, statusCode: 503);
    }
}).AllowAnonymous();

app.MapPost("/api/enroll", async (EnrollRequest request, EnrollmentService enrollment, CancellationToken ct) =>
{
    try
    {
        var response = await enrollment.RedeemAsync(request, ct);
        return Results.Ok(response);
    }
    catch (EnrollmentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).AllowAnonymous().RequireRateLimiting("enroll");

app.MapPost("/api/device/refresh", async (ClaimsPrincipal user, DeviceAuthService auth, CancellationToken ct) =>
{
    var value = user.FindFirstValue(ParentalClaims.DeviceId);
    if (!Guid.TryParse(value, out var deviceId))
        return Results.Unauthorized();
    var (_, token) = await auth.RefreshAsync(deviceId, ct);
    return Results.Ok(new DeviceRefreshResponse(token, DateTime.UtcNow.AddDays(365)));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/children/{childId:guid}/state", async (Guid childId, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    var state = await locks.GetStateAsync(family.FamilyId, childId, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/lock", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Lock, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/unlock", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Unlock, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/bonus", async (Guid childId, BonusRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Bonus, request.Minutes, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/pin", async (Guid childId, PinRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await locks.SetPinAsync(family.FamilyId, childId, request.Pin, ct);
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/pin/clear", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await locks.ClearPinAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/device/override", async (DeviceOverrideRequest request, ClaimsPrincipal user, ParentalDbContext db, LockService locks, CancellationToken ct) =>
{
    var value = user.FindFirstValue(ParentalClaims.DeviceId);
    if (!Guid.TryParse(value, out var deviceId)) return Results.Unauthorized();
    var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked, ct);
    if (device is null) return Results.Unauthorized();
    try
    {
        var state = await locks.ApplyDeviceOverrideAsync(device, request, ct);
        return Results.Ok(state);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/children/{childId:guid}/websites", async (Guid childId, WebsiteRuleRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var rule = await rules.UpsertWebsiteRuleAsync(family.FamilyId, childId, family.ParentId, request.Domain, request.Blocked, request.Mode, request.ScheduleId, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { id = rule.Id });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/websites/{ruleId:guid}/delete", async (Guid childId, Guid ruleId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await rules.RemoveWebsiteRuleAsync(family.FamilyId, childId, family.ParentId, ruleId, ct);
    await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/apps", async (Guid childId, AppRuleRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var rule = await rules.UpsertAppRuleAsync(family.FamilyId, childId, family.ParentId, request.Identity, request.DisplayName, request.Publisher, request.Blocked, request.Mode, request.ScheduleId, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { id = rule.Id });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/apps/{ruleId:guid}/delete", async (Guid childId, Guid ruleId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await rules.RemoveAppRuleAsync(family.FamilyId, childId, family.ParentId, ruleId, ct);
    await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/item-bonus", async (Guid childId, ItemBonusRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await rules.AddItemBonusAsync(family.FamilyId, childId, family.ParentId, request.RuleType, request.Identity, request.Minutes, request.AppliedUtc, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/screen-time-limit", async (Guid childId, ScreenTimeLimitRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, UsageService usage, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await usage.SetLimitAsync(family.FamilyId, childId, family.ParentId, request.Enabled, request.SpokenWarnings,
            request.Monday, request.Tuesday, request.Wednesday, request.Thursday, request.Friday, request.Saturday, request.Sunday, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/app-limit", async (Guid childId, AppLimitRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await rules.SetAppLimitAsync(family.FamilyId, childId, family.ParentId, request.Identity, request.DisplayName, request.Publisher, request.Minutes, request.AlwaysAllowed, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/alerts/{alertId:guid}/read", async (Guid childId, Guid alertId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await alerts.MarkReadAsync(family.FamilyId, childId, alertId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/alerts/read-all", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await alerts.MarkAllReadAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/categories/{key}", async (Guid childId, string key, CategoryToggleRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        await rules.ToggleCategoryAsync(family.FamilyId, childId, key, request.Enabled, ct);
        await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/device/visits", async (VisitBatchRequest request, ClaimsPrincipal user, ParentalDbContext db, RuleService rules, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await rules.SyncVisitsAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/apps/inventory", async (AppInventoryRequest request, ClaimsPrincipal user, ParentalDbContext db, RuleService rules, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await rules.SyncAppInventoryAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/block-events", async (BlockEventRequest request, ClaimsPrincipal user, ParentalDbContext db, RuleService rules, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await rules.SyncBlockEventsAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/usage", async (UsageBatchRequest request, ClaimsPrincipal user, ParentalDbContext db, UsageService usage, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await usage.SyncUsageAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/tamper", async (TamperEventRequest request, ClaimsPrincipal user, ParentalDbContext db, RuleService rules, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await rules.SyncTamperEventsAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/device/categories/{key}", async (string key, int? since, ClaimsPrincipal user, ParentalDbContext db, RuleService rules, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    try
    {
        var delta = await rules.GetCategoryDeltaAsync(key, since ?? 0, device.FamilyId, device.ChildId, ct);
        return Results.Ok(delta);
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/enrollment-codes", async (EnrollmentCodeRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, EnrollmentService enrollment, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var (code, expires) = await enrollment.CreateCodeAsync(family.FamilyId, request.ChildId, family.ParentId, ct);
        return Results.Ok(new { code, expiresUtc = expires });
    }
    catch (EnrollmentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/i/{token}/status", async (string token, InstallService installs, CancellationToken ct) =>
{
    var status = await installs.StatusAsync(token, ct);
    return Results.Ok(new { status = status.Status, childName = status.ChildName, deviceName = status.DeviceName, expiresUtc = status.ExpiresUtc, error = status.Error });
}).AllowAnonymous();

app.MapGet("/i/{token}/download", async (string token, InstallService installs, ReleaseCatalog releases, CancellationToken ct) =>
{
    var status = await installs.StatusAsync(token, ct);
    if (status.Status != "pending") return Results.NotFound(new { error = status.Error ?? "This install link has already been used." });
    var link = await installs.FindAsync(token, ct);
    if (link is null) return Results.NotFound();
    var release = releases.Current();
    if (release?.SetupPath is null || !File.Exists(release.SetupPath))
        return Results.NotFound(new { error = "The installer is not published on this server yet." });
    var code = installs.UnprotectCode(link);
    var fileName = string.IsNullOrWhiteSpace(code) ? $"OpControlParental-Setup-{release.Version}.exe" : $"OpControlParental-{code}.exe";
    return Results.File(release.SetupPath, "application/vnd.microsoft.portable-executable", fileName, enableRangeProcessing: true);
}).AllowAnonymous();

app.MapGet("/i/{token}/qr.png", (string token, InstallService installs) =>
{
    var url = $"{installs.BaseUrl}/i/{token}";
    using var generator = new QRCoder.QRCodeGenerator();
    using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
    var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
    return Results.File(png, "image/png");
}).AllowAnonymous();

app.MapGet("/downloads/setup", (ReleaseCatalog releases) =>
{
    var release = releases.Current();
    if (release?.SetupPath is null || !File.Exists(release.SetupPath)) return Results.NotFound(new { error = "No installer is published yet." });
    return Results.File(release.SetupPath, "application/vnd.microsoft.portable-executable", $"OpControlParental-Setup-{release.Version}.exe", enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/downloads/msi", (ReleaseCatalog releases) =>
{
    var release = releases.Current();
    if (release?.MsiPath is null || !File.Exists(release.MsiPath)) return Results.NotFound(new { error = "No MSI is published yet." });
    return Results.File(release.MsiPath, "application/octet-stream", $"OpControlParental-{release.Version}.msi", enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/api/releases", (ReleaseCatalog releases) =>
{
    var current = releases.CurrentInfo();
    return Results.Ok(new { current, all = releases.All().Select(r => r.ToDto(null)) });
}).RequireAuthorization();

app.MapPost("/api/install-links", async (InstallLinkCreateRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InstallService installs, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var created = await installs.CreateAsync(family.FamilyId, request.ChildId, family.ParentId, request.Platform, ct);
        return Results.Ok(created);
    }
    catch (EnrollmentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapGet("/api/install-links", async (FamilyScope family, InstallService installs, CancellationToken ct) =>
    Results.Ok(await installs.ListAsync(family.FamilyId, ct))).RequireAuthorization();

app.MapPost("/api/install-links/{linkId:guid}/revoke", async (Guid linkId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InstallService installs, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var ok = await installs.RevokeAsync(family.FamilyId, linkId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/devices/{deviceId:guid}/update", async (Guid deviceId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, UpdateService updates, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    try
    {
        var update = await updates.RequestAsync(family.FamilyId, deviceId, family.ParentId, ct);
        return Results.Ok(new { ok = true, id = update.Id, toVersion = update.ToVersion });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapGet("/api/device/update", async (ClaimsPrincipal user, ParentalDbContext db, UpdateService updates, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    var info = updates.BuildInfo(device);
    return Results.Ok(info);
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/device/update/download", async (string version, ClaimsPrincipal user, ParentalDbContext db, ReleaseCatalog releases, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    var release = releases.Get(version);
    if (release?.MsiPath is null || !File.Exists(release.MsiPath)) return Results.NotFound();
    return Results.File(release.MsiPath, "application/octet-stream", $"OpControlParental-{release.Version}.msi", enableRangeProcessing: true);
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/update/health", async (DeviceUpdateHealthRequest request, ClaimsPrincipal user, ParentalDbContext db, UpdateService updates, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    var result = await updates.RecordHealthAsync(device, request, ct);
    return Results.Ok(new { ok = true, id = result.Id });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/unaccounted", async (UnaccountedBatchRequest request, ClaimsPrincipal user, ParentalDbContext db, UsageService usage, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    await usage.SyncUnaccountedAsync(device, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/uninstall", async (UninstallReportRequest request, ClaimsPrincipal user, ParentalDbContext db, AlertService alerts, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    db.ClientTamperEvents.Add(new ClientTamperEvent
    {
        FamilyId = device.FamilyId,
        ChildId = device.ChildId,
        DeviceId = device.Id,
        Kind = "uninstall-attempted",
        Detail = request.Detail is { Length: > 500 } ? request.Detail[..500] : request.Detail,
        OccurredUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync(ct);
    await alerts.AddAsync(device.FamilyId, device.ChildId, device.Id, "tampering",
        "Uninstall attempted",
        $"The Parental client was about to be removed from {device.Name}." + (string.IsNullOrWhiteSpace(request.Detail) ? "" : $" {request.Detail}"),
        TimeSpan.FromMinutes(5), ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.Run();

static async Task<Device?> DeviceFromClaimsAsync(ClaimsPrincipal user, ParentalDbContext db, CancellationToken ct)
{
    var value = user.FindFirstValue(ParentalClaims.DeviceId);
    if (!Guid.TryParse(value, out var deviceId)) return null;
    return await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && !d.IsRevoked, ct);
}

public sealed record BonusRequest(int Minutes);
public sealed record WebsiteRuleRequest(string Domain, bool Blocked, RuleMode Mode, Guid? ScheduleId);
public sealed record AppRuleRequest(string Identity, string DisplayName, string? Publisher, bool Blocked, RuleMode Mode, Guid? ScheduleId);
public sealed record CategoryToggleRequest(bool Enabled);
public sealed record EnrollmentCodeRequest(Guid ChildId);
public sealed record PinRequest(string Pin);
public sealed record ScreenTimeLimitRequest(bool Enabled, bool SpokenWarnings, int Monday, int Tuesday, int Wednesday, int Thursday, int Friday, int Saturday, int Sunday);
public sealed record AppLimitRequest(string Identity, string DisplayName, string? Publisher, int? Minutes, bool AlwaysAllowed);
