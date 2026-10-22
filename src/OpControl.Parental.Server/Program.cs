// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Net.WebSockets;
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
    options.AddFixedWindowLimiter("remote", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("messages", limiter =>
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
builder.Services.AddLocalization();
builder.Services.Configure<BrandOptions>(builder.Configuration.GetSection("Branding"));
builder.Services.AddSingleton<BuildInfo>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<EmailRenderer>();
builder.Services.AddScoped<EmailSender>();
builder.Services.AddScoped<LanguageService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<ServerAdminService>();
builder.Services.AddScoped<TeamService>();
builder.Services.AddScoped<SiteReviewService>();
builder.Services.AddScoped<ParentPreferenceService>();
builder.Services.AddScoped<RemoteSessionService>();
builder.Services.AddSingleton<RemoteSessionRegistry>();
builder.Services.AddHostedService<RemoteSessionSweeper>();
builder.Services.AddHttpClient("sitereview", client =>
{
    client.Timeout = TimeSpan.FromSeconds(6);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("OpControlParentalSiteReview/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression = System.Net.DecompressionMethods.All
});
builder.Services.AddScoped<CameraConsentService>();
builder.Services.AddScoped<FamilyScope>();
builder.Services.AddScoped<DeviceAuthService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<InstallService>();
builder.Services.AddScoped<UpdateService>();
builder.Services.AddScoped<LockService>();
builder.Services.AddScoped<RuleService>();
builder.Services.AddScoped<UsageService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<ChildSummaryService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DashboardEventBus>();
builder.Services.AddSingleton<ReleaseCatalog>();
builder.Services.AddSingleton<SetupTokenService>();
builder.Services.AddSingleton<PendingSetupStore>();
builder.Services.AddSingleton<BakedReleaseService>();
builder.Services.AddScoped<ServerUpdateService>();
builder.Services.AddSingleton<DevicePresenceService>();
builder.Services.AddHostedService<LockExpiryService>();
builder.Services.AddHostedService<CategoryRefreshService>();
builder.Services.AddHostedService<ScheduleTransitionService>();
builder.Services.AddHostedService<UsageLimitService>();
builder.Services.AddHostedService<UpdateCheckerService>();

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

app.Services.GetRequiredService<BakedReleaseService>().EnsureCopied();

{
    using var setupScope = app.Services.CreateScope();
    var setupDb = setupScope.ServiceProvider.GetRequiredService<ParentalDbContext>();
    var setupLogger = setupScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var storedBaseUrl = await setupDb.ServerSettings.AsNoTracking()
        .Select(s => s.PublicBaseUrl).FirstOrDefaultAsync();
    if (!string.IsNullOrWhiteSpace(storedBaseUrl)) EmailRenderer.DatabaseBaseUrl = storedBaseUrl.TrimEnd('/');
    var hasServerAdmin = await setupDb.Users.AnyAsync(u => u.IsServerAdmin);
    if (!hasServerAdmin)
    {
        var setupToken = app.Services.GetRequiredService<SetupTokenService>();
        setupLogger.LogWarning("FIRST-RUN SETUP: no server administrator exists. Open /setup and enter this one-time token: {Token}", setupToken.Token);
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

var dumpEmailPreviews = GetArgument(args, "--dump-email-previews");
if (!string.IsNullOrWhiteSpace(dumpEmailPreviews))
{
    using var scope = app.Services.CreateScope();
    var renderer = scope.ServiceProvider.GetRequiredService<EmailRenderer>();
    var directory = Path.GetFullPath(dumpEmailPreviews);
    Directory.CreateDirectory(directory);
    foreach (var kind in Enum.GetValues<EmailTemplateKind>())
    {
        foreach (var language in new[] { AppCulture.English, AppCulture.Spanish })
        {
            var rendered = renderer.RenderSample(kind, language);
            var prefix = $"{kind}-{language}";
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".subject.txt"), rendered.Subject);
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".html"), rendered.HtmlBody);
            await File.WriteAllTextAsync(Path.Combine(directory, prefix + ".txt"), rendered.TextBody);
        }
    }
    Console.WriteLine($"Wrote email previews to {directory}.");
    return;
}

var sendEmailPreview = GetArgument(args, "--send-email-preview");
if (!string.IsNullOrWhiteSpace(sendEmailPreview))
{
    using var scope = app.Services.CreateScope();
    var renderer = scope.ServiceProvider.GetRequiredService<EmailRenderer>();
    var sender = scope.ServiceProvider.GetRequiredService<EmailSender>();
    var failed = 0;
    foreach (var kind in Enum.GetValues<EmailTemplateKind>())
    {
        foreach (var language in new[] { AppCulture.English, AppCulture.Spanish })
        {
            var rendered = renderer.RenderSample(kind, language);
            var (sent, error) = await sender.SendAsync(new EmailOutgoing(sendEmailPreview, rendered));
            Console.WriteLine($"{kind} {language}: {(sent ? "sent" : "failed")}{(error is null ? "" : " - " + error)}");
            if (!sent) failed++;
        }
    }
    Environment.ExitCode = failed == 0 ? 0 : 1;
    return;
}

var sendInviteTest = GetArgument(args, "--send-invite-test");
if (!string.IsNullOrWhiteSpace(sendInviteTest))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
    var invites = scope.ServiceProvider.GetRequiredService<InvitationService>();
    var adminId = await db.Users.Where(u => u.IsServerAdmin).Select(u => u.Id).FirstOrDefaultAsync();
    if (adminId == Guid.Empty)
    {
        Console.Error.WriteLine("No server administrator account was found.");
        Environment.ExitCode = 1;
        return;
    }
    try
    {
        var created = await invites.CreateFamilyInviteAsync(adminId, sendInviteTest, null);
        Console.WriteLine($"Invitation {created.Id}: emailSent={created.EmailSent}; link={created.Link}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    return;
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    var cookie = context.Request.Cookies[AppCulture.CookieName];
    string language;
    if (AppCulture.IsSupported(cookie))
        language = AppCulture.Normalize(cookie);
    else if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue(ParentalClaims.PreferredLanguage) is { Length: > 0 } preferred)
        language = AppCulture.Normalize(preferred);
    else
        language = AppCulture.FromAcceptLanguage(context.Request.Headers.AcceptLanguage);
    var culture = AppCulture.Culture(language);
    CultureInfo.CurrentCulture = culture;
    CultureInfo.CurrentUICulture = culture;
    await next();
});
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
        var parent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == parentId);
        if (parent is null)
        {
            await context.SignOutAsync();
            context.Response.Redirect("/login");
            return;
        }
        var familyStatus = await db.Families.AsNoTracking()
            .Where(f => f.Id == parent.FamilyId)
            .Select(f => (FamilyStatus?)f.Status)
            .FirstOrDefaultAsync();
        if (familyStatus is null || familyStatus == FamilyStatus.Suspended)
        {
            await context.SignOutAsync();
            context.Response.Redirect("/login?error=suspended");
            return;
        }

        var refreshed = ParentClaimsRefresher.RefreshIfNeeded(context.User, parent);
        if (refreshed is not null)
        {
            await context.SignInAsync(IdentityConstants.ApplicationScheme, refreshed,
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
            context.User = refreshed;
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

app.MapGet("/language/{lang}", async (string lang, HttpContext context, LanguageService languages, string? returnUrl) =>
{
    var language = AppCulture.Normalize(lang);
    // The header switch is the parent's own language: save it to the profile, not just this browser.
    if (context.User.Identity?.IsAuthenticated == true
        && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parentId))
        await languages.SetParentLanguageAsync(parentId, language, context.RequestAborted);
    context.Response.Cookies.Append(AppCulture.CookieName, language, new CookieOptions
    {
        HttpOnly = false,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
        Expires = DateTimeOffset.UtcNow.AddDays(365)
    });
    var target = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/";
    return Results.Redirect(target);
}).AllowAnonymous();

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

app.MapGet("/api/website-review/favicon", async (string domain, HttpContext context, FamilyScope family, SiteReviewService review, CancellationToken ct) =>
{
    await family.RequireAsync(ParentPermissions.WebsiteRules, ct);
    var icon = await review.GetFaviconAsync(domain, ct);
    if (icon is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, max-age=604800";
    return Results.File(icon.Value.Bytes, icon.Value.ContentType);
}).RequireAuthorization();

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
        await rules.ToggleCategoryAsync(family.FamilyId, childId, family.ParentId, key, request.Enabled, ct);
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
    if (status.Status != "pending") return Results.NotFound(new { error = status.Error ?? AppText.Get("Error.InstallLinkUsed") });
    var link = await installs.FindAsync(token, ct);
    if (link is null) return Results.NotFound();
    var release = releases.CurrentSetup();
    if (release?.SetupPath is null || !File.Exists(release.SetupPath))
        return Results.NotFound(new { error = AppText.Get("Error.InstallerNotPublished") });
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
    if (release?.SetupPath is null || !File.Exists(release.SetupPath)) return Results.NotFound(new { error = AppText.Get("Error.NoInstaller") });
    return Results.File(release.SetupPath, "application/vnd.microsoft.portable-executable", $"OpControlParental-Setup-{release.Version}.exe", enableRangeProcessing: true);
}).RequireAuthorization();

app.MapGet("/downloads/msi", (ReleaseCatalog releases) =>
{
    var release = releases.Current();
    if (release?.MsiPath is null || !File.Exists(release.MsiPath)) return Results.NotFound(new { error = AppText.Get("Error.NoMsi") });
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
        string.IsNullOrWhiteSpace(request.Detail)
            ? new AlertContent("Alert.Uninstall.Title", "Alert.Uninstall.Detail", device.Name)
            : new AlertContent("Alert.Uninstall.Title", "Alert.Uninstall.DetailMore", device.Name, request.Detail!),
        TimeSpan.FromMinutes(5), ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });


app.MapGet("/api/children/{childId:guid}/messages", async (Guid childId, FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    return Results.Ok(await messages.GetThreadAsync(family.FamilyId, childId, 300, ct));
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/messages", async (Guid childId, ParentMessageRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    try
    {
        var created = await messages.SendFromParentAsync(family.FamilyId, family.ParentId, childId, request, ct);
        return Results.Ok(created);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/messages/read", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    await messages.MarkParentReadAsync(family.FamilyId, childId, family.ParentId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/children/{childId:guid}/messages/clear", async (Guid childId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    await messages.ClearThreadAsync(family.FamilyId, childId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/messages/{messageId:guid}/delete", async (Guid messageId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    await messages.DeleteAsync(family.FamilyId, messageId, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/api/messages/unread", async (FamilyScope family, MessageService messages, CancellationToken ct) =>
{
    await family.RequireAsync(ParentPermissions.Messaging, ct);
    return Results.Ok(new { count = await messages.UnreadCountAsync(family.FamilyId, ct) });
}).RequireAuthorization();

app.MapGet("/api/device/child-summary", async (ClaimsPrincipal user, ParentalDbContext db, ChildSummaryService summaries, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await summaries.BuildAsync(device, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/device/messages", async (ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await messages.GetThreadForDeviceAsync(device, 300, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/device/messages/pending", async (ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await messages.GetPendingForDeviceAsync(device, 100, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/messages", async (DeviceMessageRequest request, ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    try
    {
        var created = await messages.SendFromChildAsync(device, request, ct);
        return Results.Ok(created);
    }
    catch (MessageRateLimitException ex)
    {
        return Results.Json(new { error = ex.Message, retryAfterSeconds = ex.RetryAfterSeconds }, statusCode: StatusCodes.Status429TooManyRequests);
    }
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName }).RequireRateLimiting("messages");

app.MapPost("/api/device/messages/{messageId:guid}/delivered", async (Guid messageId, ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await messages.MarkDeliveredAsync(device, messageId, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/messages/{messageId:guid}/seen", async (Guid messageId, ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await messages.MarkSeenAsync(device, messageId, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/device/messages/{messageId:guid}/ack", async (Guid messageId, ClaimsPrincipal user, ParentalDbContext db, MessageService messages, CancellationToken ct) =>
{
    var device = await DeviceFromClaimsAsync(user, db, ct);
    if (device is null) return Results.Unauthorized();
    return Results.Ok(await messages.MarkAcknowledgedAsync(device, messageId, ct));
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapGet("/api/alert-preferences", async (FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerAlertControls"));
    return Results.Ok(AlertService.ToDto(await alerts.EnsurePreferenceAsync(family.FamilyId, ct)));
}).RequireAuthorization();

app.MapPut("/api/alert-preferences", async (AlertPreferencesDto request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, AlertService alerts, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerAlertControls"));
    await alerts.SavePreferenceAsync(family.FamilyId, request, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/api/parents", async (FamilyScope family, TeamService team, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerManageParents"));
    return Results.Ok(await team.ListAsync(family.FamilyId, ct));
}).RequireAuthorization();

app.MapPut("/api/parents/{parentId:guid}/permissions", async (Guid parentId, ParentPermissionsRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, TeamService team, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerChangePermissions"));
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
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerRemoveParents"));
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
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerManageInvitations"));
    return Results.Ok(await invites.ListForFamilyAsync(family.FamilyId, ct));
}).RequireAuthorization();

app.MapPost("/api/invitations/co-parent", async (CoParentInviteRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerInviteParents"));
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
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerManageInvitations"));
    var ok = await invites.RevokeAsync(id, family.FamilyId, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = AppText.Get("Error.InvitationNotFound") });
}).RequireAuthorization();

app.MapPost("/api/invitations/{id:guid}/resend", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerManageInvitations"));
    var result = await invites.ResendAsync(id, family.FamilyId, ct);
    return result.Ok ? Results.Ok(new { ok = true, emailSent = result.EmailSent, warning = result.Error }) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapGet("/api/server/families", async (FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    return Results.Ok(await admin.ListFamiliesAsync(ct));
}).RequireAuthorization();

app.MapPost("/api/server/families/{familyId:guid}/{action}", async (Guid familyId, string action, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
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
                return Results.BadRequest(new { error = AppText.Get("Error.UnknownAction") });
        }
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization();

app.MapPost("/api/server/families/{familyId:guid}/purge", async (Guid familyId, DeleteFamilyRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    if (!string.Equals(request.Confirmation?.Trim(), request.FamilyName?.Trim(), StringComparison.Ordinal))
        return Results.BadRequest(new { error = AppText.Get("Error.ConfirmFamilyName") });
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
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    return Results.Ok(await invites.ListAllPendingAsync(ct));
}).RequireAuthorization();

app.MapPost("/api/server/invitations", async (ServerInviteRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
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
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    var ok = await invites.RevokeAsync(id, null, ct);
    return ok ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = AppText.Get("Error.InvitationNotFound") });
}).RequireAuthorization();

app.MapPost("/api/server/invitations/{id:guid}/resend", async (Guid id, HttpContext context, IAntiforgery antiforgery, FamilyScope family, InvitationService invites, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    var result = await invites.ResendAsync(id, null, ct);
    return result.Ok ? Results.Ok(new { ok = true, emailSent = result.EmailSent, warning = result.Error }) : Results.BadRequest(new { error = result.Error });
}).RequireAuthorization().RequireRateLimiting("invite");

app.MapGet("/api/server/smtp", async (FamilyScope family, EmailSender email, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    var settings = await email.EnsureSettingsAsync(ct);
    return Results.Ok(new { host = settings.SmtpHost, port = settings.SmtpPort, useSsl = settings.SmtpUseSsl,
        username = settings.SmtpUsername, fromAddress = settings.FromAddress, fromName = settings.FromName,
        hasPassword = !string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected) });
}).RequireAuthorization();

app.MapPut("/api/server/smtp", async (ServerSmtpRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, EmailSender email, ServerAdminService admin, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    await email.SaveSettingsAsync(request.Host, request.Port, request.UseSsl, request.Username, request.Password, request.FromAddress, request.FromName, ct);
    await admin.AuditAsync(access.ParentId, access.DisplayName, "server.smtp.update", null, request.Host, ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/api/server/audit", async (FamilyScope family, ServerAdminService admin, CancellationToken ct) =>
{
    var access = await family.AccessAsync(ct);
    if (!access.IsServerAdmin) throw new PermissionDeniedException(AppText.Get("Error.ServerAdminOnly"));
    return Results.Ok(await admin.RecentAuditAsync(100, ct));
}).RequireAuthorization();

app.MapGet("/api/remote/camera-consent", async (FamilyScope family, CameraConsentService consent, CancellationToken ct) =>
{
    await family.RequireAsync(ParentPermissions.RemoteView, ct);
    return Results.Ok(await consent.StatusAsync(family.ParentId, ct));
}).RequireAuthorization();

app.MapPost("/api/remote/camera-consent", async (CameraConsentAcceptRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, CameraConsentService consent, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.RemoteView, ct);
    if (!request.Accepted || request.Version != CameraConsentService.CurrentVersion)
        return Results.BadRequest(new { error = AppText.Get("Error.CameraConsentNotAccepted") });
    await consent.AcceptAsync(family.FamilyId, family.ParentId,
        context.Connection.RemoteIpAddress?.ToString(), context.Request.Headers.UserAgent.ToString(), ct);
    return Results.Ok(await consent.StatusAsync(family.ParentId, ct));
}).RequireAuthorization();

app.MapPost("/api/remote/sessions", async (RemoteSessionCreateRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RemoteSessionService remote, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.RemoteView, ct);
    try
    {
        var created = await remote.CreateAsync(family.FamilyId, family.ParentId, request.DeviceId, request.Kind, ct);
        return Results.Ok(created);
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization().RequireRateLimiting("remote");

app.MapPost("/api/remote/sessions/{sessionId:guid}/stop", async (Guid sessionId, HttpContext context, IAntiforgery antiforgery, FamilyScope family, RemoteSessionService remote, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.RemoteView, ct);
    await remote.StopAsync(sessionId, family.FamilyId, "parent-stopped", ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapGet("/ws/remote/{sessionId:guid}", async (HttpContext context, Guid sessionId, FamilyScope family, ParentalDbContext db, RemoteSessionRegistry registry, CancellationToken ct) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var session = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.FamilyId == family.FamilyId, ct);
    if (session is null || session.Status == RemoteSessionStatus.Ended)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await family.RequireAsync(ParentPermissions.RemoteView, ct);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.AttachParentAsync(sessionId, socket, ct);
}).RequireAuthorization();

app.MapGet("/ws/device-remote/{sessionId:guid}", async (HttpContext context, Guid sessionId, ClaimsPrincipal user, ParentalDbContext db, RemoteSessionRegistry registry, CancellationToken ct) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    var value = user.FindFirstValue(ParentalClaims.DeviceId);
    if (!Guid.TryParse(value, out var deviceId))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    var session = await db.RemoteSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.DeviceId == deviceId, ct);
    if (session is null || session.Status == RemoteSessionStatus.Ended)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await registry.AttachDeviceAsync(sessionId, socket, ct);
}).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = DeviceTokenAuthenticationHandler.SchemeName });

app.MapPost("/api/devices/{deviceId:guid}/notices", async (Guid deviceId, DeviceNoticeRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ParentalDbContext db, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var access = await family.AccessAsync(ct);
    if (!access.IsOwner) throw new PermissionDeniedException(AppText.Get("Error.OwnerNotice"));
    var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == family.FamilyId, ct);
    if (device is null) return Results.NotFound(new { error = AppText.Get("Error.DeviceNotFoundFamily") });
    device.ScreenNoticeEnabled = request.ScreenNotice;
    device.CameraNoticeEnabled = request.CameraNotice;
    device.HasCameraLightOrShutter = request.HasCameraLightOrShutter;
    db.OverrideEvents.Add(new OverrideEvent
    {
        FamilyId = family.FamilyId,
        ChildId = device.ChildId,
        DeviceId = device.Id,
        CreatedByParentId = access.ParentId,
        Option = "remote-notice-changed",
        Detail = $"{device.Name}|{(request.ScreenNotice ? "1" : "0")}|{(request.CameraNotice ? "1" : "0")}|{(request.HasCameraLightOrShutter ? "1" : "0")}",
        CreatedUtc = DateTime.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { ok = true });
}).RequireAuthorization();

app.MapPost("/api/devices/{deviceId:guid}/timezone", async (Guid deviceId, DeviceTimeZoneRequest request, HttpContext context, IAntiforgery antiforgery, FamilyScope family, ParentalDbContext db, CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await family.RequireAsync(ParentPermissions.ManageDevices, ct);
    var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId && d.FamilyId == family.FamilyId, ct);
    if (device is null) return Results.NotFound(new { error = AppText.Get("Error.DeviceNotFoundFamily") });
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
public sealed record DeviceNoticeRequest(bool ScreenNotice, bool CameraNotice, bool HasCameraLightOrShutter);
public sealed record RemoteSessionCreateRequest(Guid DeviceId, RemoteSessionKind Kind);
public sealed record CameraConsentAcceptRequest(bool Accepted, int Version);
