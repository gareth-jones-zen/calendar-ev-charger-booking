using PyCompat;
using WinBackground;

namespace EvChargerBooking;

/// <summary>A run through Windows Task Scheduler (WinBackground).
/// <para>A task runs `run --hook --scheduled` every every_minutes (15 by default; 2.1.0) and at logon, and a scheduled
/// run outside hours (and at weekends, with weekdays) stops at once without logging. So an "EV" added to tomorrow's
/// Travel block in the afternoon is booked that afternoon, and an approval reaches the calendar within minutes; until
/// 2.1.0 it ran once a day, so the next morning's run was too late for the next day. every_minutes = 0 runs daily at
/// daily_at instead. A run with nothing to do costs a few Graph calls; Edge opens only to book. It runs as you, only while you're logged on, needs no admin rights, and runs
/// calendar-ev-charger-booking-background.exe directly, so no window appears (the browser runs headless). The same task name as the
/// Python version, so switching replaces the Python task rather than adding a second.</para></summary>
public static class Autostart
{
    public const string TaskName = "calendar-ev-charger-booking run";
    public const string ConsoleExe = "calendar-ev-charger-booking.exe";
    public const string BackgroundExe = "calendar-ev-charger-booking-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Books EV charging when the calendar asks for it, and applies approvals (calendar-ev-charger-booking run).");

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "run", "--hook", "--scheduled"]);
    }

    /// <summary>From [schedule] (the library builds the triggers and records their fingerprint in the task, so
    /// `autostart status` can tell when the settings change): by default every every_minutes (or daily at daily_at
    /// when it's 0) and at logon, as a job stopped after 30 minutes, started now.</summary>
    public static TaskSchedule Schedule(Settings settings) => settings.Schedule.ToTaskSchedule(TaskKind.Job);

    /// <summary>When it's to run: the scheduled run checks [schedule]'s hours, days and end_date, so the task can
    /// simply repeat.</summary>
    public static bool InHours(Settings settings, DateTime now) => settings.Schedule.IsActive(now);

    /// <summary>The line a scheduled run logs while the settings still use the old top-level schedule keys.</summary>
    public const string OldScheduleNote =
        "settings: every_minutes, daily_at, hours and weekdays at the top level are the old form; move them to [schedule] (see the README)";

    /// <summary>"every 15 minutes, 07:00-19:00 Mon-Fri, and at logon": the schedule in words.</summary>
    public static string Describe(Settings settings)
    {
        var s = settings.Schedule;
        var parts = new List<string>();
        if (s.EveryMinutes is > 0 and var m)
        {
            var gate = s.Hours is null ? "" : $", {settings.HoursFrom}-{settings.HoursTo}";
            if (settings.Weekdays)
                gate += " Mon-Fri";
            else if (s.Days is { Count: < 7 } days)
                gate += " " + string.Join(",", days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]));
            parts.Add($"every {m} minutes{gate}");
        }
        else if (s.DailyAt is { } at)
            parts.Add($"daily at {at}");
        if (s.AfterUnlockMinutes > 0)
            parts.Add($"{s.AfterUnlockMinutes} minute(s) after each unlock");
        if (s.AtLogon)
            parts.Add("at logon");
        var text = parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return s.EndDate is { } end ? $"{text} (until {end:yyyy-MM-dd})" : text;
    }
}
