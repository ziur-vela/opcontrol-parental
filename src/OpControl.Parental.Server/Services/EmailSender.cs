using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class EmailSender
{
    private readonly ParentalDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(ParentalDbContext db, IDataProtectionProvider protection, ILogger<EmailSender> logger)
    {
        _db = db;
        _protector = protection.CreateProtector("OpControl.Parental.ServerSmtp");
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

    public async Task<(bool Sent, string? Error)> SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        var settings = await EnsureSettingsAsync(ct);
        if (!IsConfigured(settings))
            return (false, "Server SMTP is not configured.");

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress!, settings.FromName ?? "Parental OpControl"),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(to);

            using var client = new SmtpClient(settings.SmtpHost, settings.SmtpPort)
            {
                EnableSsl = settings.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false
            };
            if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
                client.Credentials = new NetworkCredential(settings.SmtpUsername, UnprotectPassword(settings) ?? "");
            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Email sent to {Recipient}: {Subject}", to, subject);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Email to {Recipient} failed", to);
            return (false, ex.Message);
        }
    }
}
