using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class EnrollmentException(string message) : Exception(message);

public sealed class EnrollmentService
{
    private readonly ParentalDbContext _db;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(ParentalDbContext db, ILogger<EnrollmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<(string Code, DateTime ExpiresUtc)> CreateCodeAsync(Guid familyId, Guid childId, Guid parentId, CancellationToken ct = default)
    {
        var childExists = await _db.Children.AnyAsync(c => c.Id == childId && c.FamilyId == familyId, ct);
        if (!childExists)
            throw new EnrollmentException("Child not found in this family.");

        var code = SecretToken.NewCode();
        var now = DateTime.UtcNow;
        _db.EnrollmentCodes.Add(new EnrollmentCode
        {
            FamilyId = familyId,
            ChildId = childId,
            CreatedByParentId = parentId,
            CodeHash = SecretToken.Hash(SecretToken.NormalizeCode(code)),
            CreatedUtc = now,
            ExpiresUtc = now.AddMinutes(15)
        });
        await _db.SaveChangesAsync(ct);
        return (code, now.AddMinutes(15));
    }

    public async Task<EnrollResponse> RedeemAsync(EnrollRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            throw new EnrollmentException("Enrollment code is required.");
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 120)
            throw new EnrollmentException("Device name is required (max 120 characters).");
        if (!Enum.IsDefined(request.Platform))
            throw new EnrollmentException("Unsupported device platform.");

        var normalized = SecretToken.NormalizeCode(request.Code);
        var hash = SecretToken.Hash(normalized);
        var now = DateTime.UtcNow;

        var code = await _db.EnrollmentCodes
            .FirstOrDefaultAsync(c => c.CodeHash == hash && c.UsedUtc == null && c.ExpiresUtc > now, ct)
            ?? throw new EnrollmentException("Enrollment code is invalid or has expired.");

        var child = await _db.Children
            .FirstOrDefaultAsync(c => c.Id == code.ChildId && c.FamilyId == code.FamilyId, ct)
            ?? throw new EnrollmentException("Child not found.");

        var token = SecretToken.NewToken();
        var device = new Device
        {
            FamilyId = code.FamilyId,
            ChildId = code.ChildId,
            Name = request.DeviceName.Trim(),
            Platform = request.Platform,
            OsVersion = request.OsVersion,
            TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId!,
            EnrolledUtc = now,
            LastSeenUtc = now,
            CredentialHash = SecretToken.Hash(token),
            CredentialIssuedUtc = now,
            CredentialExpiresUtc = now.AddDays(365)
        };

        code.UsedUtc = now;
        _db.Devices.Add(device);
        if (!string.IsNullOrWhiteSpace(request.OsAccountName))
        {
            _db.DeviceAccounts.Add(new DeviceAccount
            {
                FamilyId = code.FamilyId,
                DeviceId = device.Id,
                OsAccountName = request.OsAccountName!.Trim(),
                IsMonitored = true
            });
        }

        await _db.SaveChangesAsync(ct);
        var lockState = await _db.LockStates.FirstOrDefaultAsync(s => s.ChildId == code.ChildId, ct);
        if (lockState is null)
        {
            lockState = new LockState { FamilyId = code.FamilyId, ChildId = code.ChildId, UpdatedUtc = DateTime.UtcNow };
            _db.LockStates.Add(lockState);
            await _db.SaveChangesAsync(ct);
        }
        var evaluation = LockEvaluator.Evaluate(lockState, Array.Empty<Schedule>(), TimeZoneInfo.Utc, DateTime.UtcNow);
        var state = new LockStateDto(lockState.ChildId, evaluation.IsLocked, lockState.ManualLock, lockState.UnlockUntilUtc, lockState.BonusUntilUtc, DateTime.UtcNow, evaluation.Reason);

        _logger.LogInformation("Device {DeviceId} enrolled for child {ChildId} in family {FamilyId}", device.Id, child.Id, child.FamilyId);

        return new EnrollResponse(device.Id, device.ChildId, device.Name, token, device.CredentialExpiresUtc!.Value, state);
    }
}
