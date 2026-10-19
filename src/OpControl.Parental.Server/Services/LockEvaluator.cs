using OpControl.Parental.Server.Data;

namespace OpControl.Parental.Server.Services;

public sealed record LockEvaluation(bool IsLocked, string Reason);

public static class LockEvaluator
{
    public static LockEvaluation Evaluate(LockState state, IReadOnlyCollection<Schedule> schedules, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (state.BonusUntilUtc is { } bonus && bonus > nowUtc)
            return new LockEvaluation(false, "bonus");
        if (state.UnlockUntilUtc is { } unlock && unlock > nowUtc)
            return new LockEvaluation(false, "override");
        if (state.ManualLock)
            return new LockEvaluation(true, "manual");
        if (IsScheduleActive(schedules, tz, nowUtc))
            return new LockEvaluation(true, "schedule");
        return new LockEvaluation(false, "unlocked");
    }

    public static bool IsScheduleActive(IReadOnlyCollection<Schedule> schedules, TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minute = local.Hour * 60 + local.Minute;
        var day = (int)local.DayOfWeek;
        var previousDay = (day + 6) % 7;

        foreach (var schedule in schedules)
        {
            if (schedule.StartMinutes <= schedule.EndMinutes)
            {
                if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes && minute < schedule.EndMinutes)
                    return true;
            }
            else
            {
                if ((schedule.DaysMask & (1 << day)) != 0 && minute >= schedule.StartMinutes)
                    return true;
                if ((schedule.DaysMask & (1 << previousDay)) != 0 && minute < schedule.EndMinutes)
                    return true;
            }
        }

        return false;
    }

    public static DateTime? CurrentScheduleEnd(IReadOnlyCollection<Schedule> schedules, TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var minute = local.Hour * 60 + local.Minute;
        var day = (int)local.DayOfWeek;
        var previousDay = (day + 6) % 7;
        DateTime? latest = null;

        foreach (var schedule in schedules)
        {
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

            if (end is not null)
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end.Value, DateTimeKind.Unspecified), tz);
                if (latest is null || utc > latest) latest = utc;
            }
        }

        return latest;
    }
}
