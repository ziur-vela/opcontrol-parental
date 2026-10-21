using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class EmailSender
{
    private readonly ParentalDbContext _db;
    private readonly IDataProtector _protector;
    private readonly IDataProtector _familyProtector;
    private readonly EmailRenderer _renderer;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(ParentalDbContext db, IDataProtectionProvider protection, EmailRenderer renderer,
        IOptions<EmailOptions> options, ILogger<EmailSender> logger)
    {
        _db = db;
        _protector = protection.CreateProtector("OpControl.Parental.ServerSmtp");
        _familyProtector = protection.CreateProtector("OpControl.Parental.Smtp");
        _renderer = renderer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ServerSettings> EnsureSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.ServerSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is not null) return settings;
        settings = new ServerSettings();
        _db.ServerSettings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public bool IsConfigured(ServerSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.SmtpHost) && !string.IsNullOrWhiteSpace(settings.FromAddress);

    public async Task SaveSettingsAsync(string? host, int port, bool useSsl, string? username, string? password, string? from, string? fromName, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(ct);
        settings.SmtpHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        settings.SmtpPort = Math.Clamp(port, 1, 65535);
        settings.SmtpUseSsl = useSsl;
        settings.SmtpUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        if (!string.IsNullOrWhiteSpace(password))
            settings.SmtpPasswordProtected = _protector.Protect(password);
        settings.FromAddress = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        settings.FromName = string.IsNullOrWhiteSpace(fromName) ? "Parental OpControl" : fromName.Trim();
        settings.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public string? UnprotectPassword(ServerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpPasswordProtected)) return null;
        try { return _protector.Unprotect(settings.SmtpPasswordProtected); }
        catch { return null; }
    }

    public async Task<(bool Sent, string? Error)> SendAsync(EmailOutgoing outgoing, FamilySettings? family = null, CancellationToken ct = default)
    {
        var (valid, validationError) = _renderer.ValidateConfiguration();
        if (!valid)
        {
            _logger.LogError("Email to {Recipient} was not sent: {Error}", outgoing.To, validationError);
            return (false, validationError);
        }

        var server = await EnsureSettingsAsync(ct);
        var useServer = IsConfigured(server);
        string? host = useServer ? server.SmtpHost : family?.SmtpHost;
        var port = useServer ? server.SmtpPort : family?.SmtpPort ?? 587;
        var useSsl = useServer ? server.SmtpUseSsl : family?.SmtpUseSsl ?? true;
        var username = useServer ? server.SmtpUsername : family?.SmtpUsername;
        var fromAddress = string.IsNullOrWhiteSpace(_options.FromAddress)
            ? server.FromAddress ?? family?.FromAddress
            : _options.FromAddress.Trim();
        var fromName = string.IsNullOrWhiteSpace(_options.FromName)
            ? server.FromName ?? "Parental OpControl"
            : _options.FromName.Trim();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
        {
            var error = AppText.Get("Email.SmtpNotConfigured");
            _logger.LogWarning("Email to {Recipient} was not sent: {Error}", outgoing.To, error);
            return (false, error);
        }

        try
        {
            using var message = EmailMime.Compose(outgoing, fromAddress, fromName, _options.ReplyTo, _renderer.Staging);
            using var client = new SmtpClient(host, port)
            {
                EnableSsl = useSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };
            if (!string.IsNullOrWhiteSpace(username))
            {
                var password = useServer ? UnprotectPassword(server) : UnprotectFamilyPassword(family);
                client.Credentials = new NetworkCredential(username, password ?? "");
            }
            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Email sent to {Recipient}: {Subject}", outgoing.To, message.Subject);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email to {Recipient} failed", outgoing.To);
            return (false, ex.Message);
        }
    }

    private string? UnprotectFamilyPassword(FamilySettings? family)
    {
        if (family is null || string.IsNullOrWhiteSpace(family.SmtpPasswordProtected)) return null;
        try
        {
            return _familyProtector.Unprotect(family.SmtpPasswordProtected);
        }
        catch { return null; }
    }
}
