// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Microsoft.EntityFrameworkCore;
using OpControl.Parental.Contracts;
using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed class ScheduleTransitionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScheduleTransitionService> _logger;

    public ScheduleTransitionService(IServiceScopeFactory scopes, ILogger<ScheduleTransitionService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Schedule transition evaluation failed");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EvaluateAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ParentalDbContext>();
        var now = DateTime.UtcNow;
        var children = await db.Children.AsNoTracking().Select(c => new { c.Id, c.FamilyId }).ToListAsync(ct);

        foreach (var child in children)
        {
            var tzId = await db.Devices.AsNoTracking()
                .Where(d => d.ChildId == child.Id)
                .Select(d => d.TimeZoneId)
                .FirstOrDefaultAsync(ct);
            var tz = Resolve(tzId);
            var schedules = await db.Schedules.AsNoTracking()
                .Where(s => s.ChildId == child.Id)
                .Select(s => new ScheduleDto(s.Id, s.Name, s.DaysMask, s.StartMinutes, s.EndMinutes, s.IsEnabled))
                .ToListAsync(ct);
            var active = LockRules.ActiveSchedule(schedules, tz, now);

            var state = await db.ScheduleRuntimeStates.FirstOrDefaultAsync(s => s.ChildId == child.Id, ct);
            if (state is null)
            {
                state = new ScheduleRuntimeState { ChildId = child.Id, ActiveScheduleId = active?.Id, ActiveScheduleName = active?.Name, UpdatedUtc = now };
                db.ScheduleRuntimeStates.Add(state);
                continue;
            }

            if (state.ActiveScheduleId == active?.Id && state.ActiveScheduleName == active?.Name)
                continue;

            var previousName = state.ActiveScheduleName;
            state.ActiveScheduleId = active?.Id;
            state.ActiveScheduleName = active?.Name;
            state.UpdatedUtc = now;

            if (active is not null)
            {
                db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = child.FamilyId,
                    ChildId = child.Id,
                    Option = "schedule-start",
                    Detail = active.Name,
                    CreatedUtc = now
                });
            }
            else
            {
                db.OverrideEvents.Add(new OverrideEvent
                {
                    FamilyId = child.FamilyId,
                    ChildId = child.Id,
                    Option = "schedule-end",
                    Detail = previousName,
                    CreatedUtc = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static TimeZoneInfo Resolve(string? id) => TimeZoneResolver.Resolve(id);
}
