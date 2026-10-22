namespace OpControl.Parental.Contracts;

public sealed record LockEvaluation(bool IsLocked, string Reason, string? ScheduleName, DateTime? EndsUtc);

public sealed record LockCycle(DateTime Utc, string? ScheduleName);

public static class LockRules
{
    public static LockEvaluation Evaluate(DeviceStateDto state, TimeZoneInfo tz, DateTime nowUtc) =>
        Evaluate(state.ManualLock, state.UnlockUntilUtc, state.BonusUntilUtc, state.Schedules, tz, nowUtc, state.DailyLimit);

    public static LockEvaluation Evaluate(
        bool manualLock,
        DateTime? unlockUntilUtc,
        DateTime? bonusUntilUtc,
        IReadOnlyList<ScheduleDto> schedules,
        TimeZoneInfo tz,
        DateTime nowUtc,
        DailyLimitDto? dailyLimit = null)
    {
        if (bonusUntilUtc is { } bonus && bonus > nowUtc)
            return new LockEvaluation(false, "bonus", null, bonus);
        if (unlockUntilUtc is { } unlock && unlock > nowUtc)
            return new LockEvaluation(false, "override", null, unlock);
        if (manualLock)
            return new LockEvaluation(true, "manual", null, null);

        var schedule = ActiveSchedule(schedules, tz, nowUtc);
        if (schedule is not null)
            return new LockEvaluation(true, "schedule", schedule.Name, CurrentScheduleEnd(schedule, tz, nowUtc));

        if (DailyLimitReached(dailyLimit, nowUtc) && dailyLimit is not null)
            return new LockEvaluation(true, "daily-limit", null, dailyLimit.DayEndUtc);

        return new LockEvaluation(false, "unlocked", null, null);
    }

    public static bool DailyLimitReached(DailyLimitDto? dailyLimit, DateTime nowUtc) =>
        dailyLimit is { Enabled: true, TodayMinutes: > 0 }
        && nowUtc >= dailyLimit.DayStartUtc
        && nowUtc < dailyLimit.DayEndUtc
        && dailyLimit.UsedTodaySeconds >= dailyLimit.TodayMinutes * 60;

    public static int? DailyLimitRemainingSeconds(DailyLimitDto? dailyLimit, DateTime nowUtc)
    {
        if (dailyLimit is not { Enabled: true, TodayMinutes: > 0 }) return null;
        if (nowUtc >= dailyLimit.DayEndUtc) return dailyLimit.TodayMinutes * 60;
        if (nowUtc < dailyLimit.DayStartUtc) return dailyLimit.TodayMinutes * 60;
        return Math.Max(0, dailyLimit.TodayMinutes * 60 - dailyLimit.UsedTodaySeconds);
    }

    public static DateTime? NextLockUtc(
        bool manualLock,
        DateTime? unlockUntilUtc,
        DateTime? bonusUntilUtc,
        IReadOnlyList<ScheduleDto> schedules,
        TimeZoneInfo tz,
        DateTime nowUtc)
    {
        if (manualLock && (bonusUntilUtc is null || bonusUntilUtc <= nowUtc))
            return null;
        if (bonusUntilUtc is { } bonus && bonus > nowUtc)
            return bonus;
        if (unlockUntilUtc is { } unlock && unlock > nowUtc)
            return unlock;
        return NextScheduleStart(schedules, tz, nowUtc);
    }

    public static DateTime? NextLockCycleUtc(IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime nowUtc, DailyLimitDto? dailyLimit = null) =>
        NextLockCycle(schedules, tz, nowUtc, dailyLimit).Utc;

    public static LockCycle NextLockCycle(IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime nowUtc, DailyLimitDto? dailyLimit = null)
    {
        var active = ActiveSchedule(schedules, tz, nowUtc);
        LockCycle? best = null;

        if (active is not null)
        {
            var end = CurrentScheduleEnd(active, tz, nowUtc);
            if (end is { } value && value > nowUtc)
                best = new LockCycle(value, active.Name);
        }

        if (NextScheduleStartWithName(schedules, tz, nowUtc) is { } next && (best is null || next.Utc < best.Utc))
            best = next;

        if (dailyLimit is { Enabled: true } && dailyLimit.DayEndUtc > nowUtc && (best is null || dailyLimit.DayEndUtc < best.Utc))
            best = new LockCycle(dailyLimit.DayEndUtc, null);

        return best ?? new LockCycle(NextLocalMidnightUtc(tz, nowUtc), null);
    }

    public static DateTime NextLocalMidnightUtc(TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var utc = ToUtc(local.Date.AddDays(1), tz);
        if (utc <= nowUtc)
            utc = ToUtc(local.Date.AddDays(2), tz);
        return utc;
    }

    public static ScheduleDto? ActiveSchedule(IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minute = local.Hour * 60 + local.Minute;
        var day = (int)local.DayOfWeek;
        var previousDay = (day + 6) % 7;

        foreach (var schedule in schedules.Where(s => s.IsEnabled))
        {
            if (schedule.StartMinutes <= schedule.EndMinutes)
            {
                if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes && minute < schedule.EndMinutes)
                    return schedule;
            }
            else
            {
                if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes)
                    return schedule;
                if ((schedule.DaysMask & (1 << previousDay)) != 0 && minute < schedule.EndMinutes)
                    return schedule;
            }
        }

        return null;
    }

    public static DateTime? CurrentScheduleEnd(ScheduleDto schedule, TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minute = local.Hour * 60 + local.Minute;
        var day = (int)local.DayOfWeek;
        var previousDay = (day + 6) % 7;
        DateTime? end = null;

        if (schedule.StartMinutes <= schedule.EndMinutes)
        {
            if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes && minute < schedule.EndMinutes)
                end = local.Date.AddMinutes(schedule.EndMinutes);
        }
        else
        {
            if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes)
                end = local.Date.AddDays(1).AddMinutes(schedule.EndMinutes);
            else if ((schedule.DaysMask & (1 << previousDay)) != 0 && minute < schedule.EndMinutes)
                end = local.Date.AddMinutes(schedule.EndMinutes);
        }

        return end is null ? null : ToUtc(end.Value, tz);
    }

    public static DateTime? NextScheduleStart(IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime nowUtc) =>
        NextScheduleStartWithName(schedules, tz, nowUtc)?.Utc;

    public static LockCycle? NextScheduleStartWithName(IReadOnlyList<ScheduleDto> schedules, TimeZoneInfo tz, DateTime nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        LockCycle? best = null;

        foreach (var schedule in schedules.Where(s => s.IsEnabled))
        {
            for (var offset = 0; offset <= 8; offset++)
            {
                var date = localNow.Date.AddDays(offset);
                if ((schedule.DaysMask & (1 << (int)date.DayOfWeek)) == 0)
                    continue;
                var candidateLocal = date.AddMinutes(schedule.StartMinutes);
                var candidateUtc = ToUtc(candidateLocal, tz);
                if (candidateUtc <= nowUtc) continue;
                if (best is null || candidateUtc < best.Utc) best = new LockCycle(candidateUtc, schedule.Name);
                break;
            }
        }

        return best;
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        }
        catch (ArgumentException)
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.AddHours(1), DateTimeKind.Unspecified), tz);
        }
    }
}
