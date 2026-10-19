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

var dataProtectionPath = builder.Configuration["DataProtection:Path"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<FamilyScope>();
builder.Services.AddScoped<DeviceAuthService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<LockService>();
builder.Services.AddSingleton<DashboardEventBus>();
builder.Services.AddSingleton<DevicePresenceService>();
builder.Services.AddHostedService<LockExpiryService>();

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

app.Run();

public sealed record BonusRequest(int Minutes);
public sealed record EnrollmentCodeRequest(Guid ChildId);
