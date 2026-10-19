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
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

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
builder.Services.AddAuthorization(options => options.AddParentalPolicies());
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
    options.AddFixedWindowLimiter("invite", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("signin", limiter =>
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
builder.Services.Configure<BrandOptions>(builder.Configuration.GetSection("Branding"));
builder.Services.AddSingleton<BuildInfo>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<EmailSender>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<ServerAdminService>();
builder.Services.AddScoped<TeamService>();
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

if (args.Any(a => string.Equals(a, "--set-server-smtp-stdin", StringComparison.OrdinalIgnoreCase)))
{
    using var scope = app.Services.CreateScope();
    var email = scope.ServiceProvider.GetRequiredService<EmailSender>();
    var smtp = await System.Text.Json.JsonSerializer.DeserializeAsync<ServerSmtpRequest>(Console.OpenStandardInput(),
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (smtp is null)
    {
        Console.Error.WriteLine("No SMTP settings were provided on stdin.");
        Environment.ExitCode = 1;
        return;
    }
    await email.SaveSettingsAsync(smtp.Host, smtp.Port, smtp.UseSsl, smtp.Username, smtp.Password, smtp.FromAddress, smtp.FromName);
    Console.WriteLine("Server SMTP settings saved.");
    return;
}

var adminEmailArgument = GetArgument(args, "--set-server-admin");
if (!string.IsNullOrWhiteSpace(adminEmailArgument))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
    var email = adminEmailArgument.Trim();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower());
    if (user is null)
    {
        Console.Error.WriteLine($"No parent account found for {email}.");
        Environment.ExitCode = 1;
        return;
    }
    user.IsServerAdmin = true;
    await db.SaveChangesAsync();
    Console.WriteLine($"Server administrator granted to {user.Email} ({user.DisplayName}).");
    return;
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirstValue("device_id") is null
        && context.Request.Path.StartsWithSegments("/invite") == false
        && context.Request.Path.StartsWithSegments("/css") == false
        && context.Request.Path.StartsWithSegments("/js") == false
        && context.Request.Path.StartsWithSegments("/brand") == false
        && context.Request.Path.StartsWithSegments("/_framework") == false
        && context.Request.Path.StartsWithSegments("/_blazor") == false
        && context.Request.Path.StartsWithSegments("/health") == false
        && context.Request.Path.StartsWithSegments("/version") == false)
    {
        var db = context.RequestServices.GetRequiredService<ParentalDbContext>();
        var idValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var parentId))
        {
            await context.SignOutAsync();
            context.Response.Redirect("/login");
            return;
        }
        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == parentId)
            .Join(db.Families.AsNoTracking(), u => u.FamilyId, f => f.Id, (u, f) => new { f.Status })
            .FirstOrDefaultAsync();
        if (state is null || state.Status == FamilyStatus.Suspended)
        {
            await context.SignOutAsync();
            context.Response.Redirect("/login?error=suspended");
            return;
        }
    }
    await next();
});
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (PermissionDeniedException ex)
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) when (context.Request.Path.StartsWithSegments("/api"))
    {
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseRateLimiter();
app.UseAntiforgery();

app.MapRazorPages();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<ParentalHub>("/hubs/device");

app.MapGet("/health", async (ParentalDbContext db, BuildInfo build, CancellationToken ct) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        return Results.Ok(new { status = "ok", database = "ok", utc = DateTime.UtcNow, version = build.DisplayVersion });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "degraded", database = "error", utc = DateTime.UtcNow, detail = ex.GetType().Name, version = build.DisplayVersion }, statusCode: 503);
    }
}).AllowAnonymous();

app.MapGet("/version", (BuildInfo build) => Results.Ok(build.ToDto())).AllowAnonymous();

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
    await family.RequireAsync(ParentPermissions.ViewReports, ct);
    var state = await locks.GetStateAsync(family.FamilyId, childId, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/lock", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.LockUnlockBonus, ct);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Lock, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/unlock", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.LockUnlockBonus, ct);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Unlock, null, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/bonus", async (Guid childId, BonusRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.LockUnlockBonus, ct);
    var state = await locks.ApplyAsync(family.FamilyId, childId, family.ParentId, LockAction.Bonus, request.Minutes, ct);
    return Results.Ok(state);
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/pin", async (Guid childId, PinRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.ManagePin, ct);
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
    await family.RequireAsync(ParentPermissions.ManagePin, ct);
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
    await family.RequireAsync(ParentPermissions.WebsiteRules, ct);
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
    await family.RequireAsync(ParentPermissions.WebsiteRules, ct);
    await rules.RemoveWebsiteRuleAsync(family.FamilyId, childId, family.ParentId, ruleId, ct);
    await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/apps", async (Guid childId, AppRuleRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.AppRules, ct);
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
    await family.RequireAsync(ParentPermissions.AppRules, ct);
    await rules.RemoveAppRuleAsync(family.FamilyId, childId, family.ParentId, ruleId, ct);
    await locks.PublishDeviceStateAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/item-bonus", async (Guid childId, ItemBonusRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    access.Require(request.RuleType == "site" ? ParentPermissions.WebsiteRules : ParentPermissions.AppRules);
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
    await family.RequireAsync(ParentPermissions.SchedulesLimits, ct);
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
    await family.RequireAsync(ParentPermissions.SchedulesLimits, ct);
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
    await family.RequireAsync(ParentPermissions.ViewReports, ct);
    await alerts.MarkReadAsync(family.FamilyId, childId, alertId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/alerts/read-all", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.ViewReports, ct);
    await alerts.MarkAllReadAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/categories/{key}", async (Guid childId, string key, CategoryToggleRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RuleService rules, LockService locks, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.WebsiteRules, ct);
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
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
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
    var release = releases.CurrentSetup();
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
    var release = releases.CurrentSetup();
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
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
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
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
    var ok = await installs.RevokeAsync(family.FamilyId, linkId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/devices/{deviceId:guid}/update", async (Guid deviceId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, UpdateService updates, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
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


app.MapGet("/api/alert-preferences", async (FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can change alert controls.");
    return Results.Ok(AlertService.ToDto(await alerts.EnsurePreferenceAsync(family.FamilyId, ct)));
}).RequireAuthorization();

app.MapPut("/api/alert-preferences", async (AlertPreferencesDto request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can change alert controls.");
    await alerts.SavePreferenceAsync(family.FamilyId, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/api/parents", async (FamilyScope family, TeamService team, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can manage parents.");
    return Results.Ok(await team.ListAsync(family.FamilyId, ct));
}).RequireAuthorization();

app.MapPut("/api/parents/{parentId:guid}/permissions", async (Guid parentId, ParentPermissionsRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, TeamService team, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can change permissions.");
    try
    {
        await team.SetPermissionsAsync(family.FamilyId, parentId, request.Permissions, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapDelete("/api/parents/{parentId:guid}", async (Guid parentId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, TeamService team, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can remove parents.");
    try
    {
        await team.RemoveAsync(family.FamilyId, parentId, access.ParentId, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapGet("/api/invitations", async (FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can manage invitations.");
    return Results.Ok(await invites.ListForFamilyAsync(family.FamilyId, ct));
}).RequireAuthorization();

app.MapPost("/api/invitations/co-parent", async (CoParentInviteRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can invite parents.");
    try
    {
        var created = await invites.CreateCoParentAsync(family.FamilyId, access.ParentId, request.Email, request.Permissions, ct);
        return Results.Ok(created);
    }
    catch (InvitationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapPost("/api/invitations/{id:guid}/revoke", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can manage invitations.");
    var ok = await invites.RevokeAsync(id, family.FamilyId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Invitation not found." });
}).RequireAuthorization();

app.MapPost("/api/invitations/{id:guid}/resend", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException("Only the family owner can manage invitations.");
    var result = await invites.ResendAsync(id, family.FamilyId, ct);
    return result.Ok ? Results.Ok(new { ok = true, emailSent = result.EmailSent, warning = result.Error }) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapGet("/api/server/families", async (FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    return Results.Ok(await admin.ListFamiliesAsync(ct));
}).RequireAuthorization();

app.MapPost("/api/server/families/{familyId:guid}/{action}", async (Guid familyId, string action, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    try
    {
        switch (action.ToLowerInvariant())
        {
            case "suspend":
                await admin.SetStatusAsync(familyId, FamilyStatus.Suspended, access.ParentId, access.DisplayName, ct);
                return Results.Ok(new { ok = true });
            case "reactivate":
                await admin.SetStatusAsync(familyId, FamilyStatus.Active, access.ParentId, access.DisplayName, ct);
                return Results.Ok(new { ok = true });
            default:
                return Results.BadRequest(new { error = "Unknown action." });
        }
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/server/families/{familyId:guid}/purge", async (Guid familyId, DeleteFamilyRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    if (!string.Equals(request.Confirmation?.Trim(), request.FamilyName?.Trim(), StringComparison.Ordinal))
        return Results.BadRequest(new { error = "Type the family name exactly to confirm." });
    try
    {
        await admin.DeleteFamilyAsync(familyId, access.ParentId, access.DisplayName, ct);
        return Results.Ok(new { ok = true });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapGet("/api/server/invitations", async (FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    return Results.Ok(await invites.ListAllPendingAsync(ct));
}).RequireAuthorization();

app.MapPost("/api/server/invitations", async (ServerInviteRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    try
    {
        var created = await invites.CreateFamilyInviteAsync(access.ParentId, request.Email, request.FamilyName, ct);
        await admin.AuditAsync(access.ParentId, access.DisplayName, "family.invite", null, request.Email ?? "link-only", ct);
        return Results.Ok(created);
    }
    catch (InvitationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapPost("/api/server/invitations/{id:guid}/revoke", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    var ok = await invites.RevokeAsync(id, null, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "Invitation not found." });
}).RequireAuthorization();

app.MapPost("/api/server/invitations/{id:guid}/resend", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    var result = await invites.ResendAsync(id, null, ct);
    return result.Ok ? Results.Ok(new { ok = true, emailSent = result.EmailSent, warning = result.Error }) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapGet("/api/server/smtp", async (FamilyScope family, EmailSender email, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    var settings = await email.EnsureSettingsAsync(ct);
    return Results.Ok(new { host = settings.SmtpHost, port = settings.SmtpPort, useSsl = settings.SmtpUseSsl,
        username = settings.SmtpUsername, fromAddress = settings.FromAddress, fromName = settings.FromName,
        hasPassword = !string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected) });
}).RequireAuthorization();

app.MapPut("/api/server/smtp", async (ServerSmtpRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, EmailSender email, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    await email.SaveSettingsAsync(request.Host, request.Port, request.UseSsl, request.Username, request.Password, request.FromAddress, request.FromName, ct);
    await admin.AuditAsync(access.ParentId, access.DisplayName, "server.smtp.update", null, request.Host, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/api/server/audit", async (FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException("Server administrator only.");
    return Results.Ok(await admin.RecentAuditAsync(100, ct));
}).RequireAuthorization();

app.MapPost("/api/devices/{deviceId:guid}/timezone", async (Guid deviceId, DeviceTimeZoneRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ParentalDbContext db, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
    var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == family.FamilyId, ct);
    if (device is null) return Results.NotFound(new { error = "Device not found in this family." });
    var zone = TimeZoneResolver.Resolve(request.TimeZoneId);
    device.TimeZoneId = zone.Id;
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { ok = true, timeZoneId = zone.Id });
}).RequireAuthorization();

app.Run();

static string? GetArgument(string[] args, string name)
{
    for (var i = 0; i < args.Length; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            return args[i + 1];
    return null;
}

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
public sealed record ParentPermissionsRequest(ParentPermissions Permissions);
public sealed record CoParentInviteRequest(string? Email, ParentPermissions Permissions);
public sealed record ServerInviteRequest(string? Email, string? FamilyName);
public sealed record DeleteFamilyRequest(string? FamilyName, string? Confirmation);
public sealed record ServerSmtpRequest(string? Host, int Port, bool UseSsl, string? Username, string? Password, string? FromAddress, string? FromName);
public sealed record DeviceTimeZoneRequest(string TimeZoneId);
