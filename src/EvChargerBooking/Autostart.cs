using PyCompat;
using WinBackground;

namespace EvChargerBooking;

/// <summary>A run through Windows Task Scheduler (WinBackground).
/// <para>A task runs `run --hook --scheduled` every every_minutes (15 by default; 2.1.0) and at logon, and a scheduled
/// run outside hours (and at weekends, with weekdays) stops at once without logging. So an "EV" added to tomorrow's
/// Travel block in the afternoon is booked that afternoon, and an approval reaches the calendar within minutes; until
/// 2.1.0 it ran once a day, so the next morning's run was too late for the next day. every_minutes = 0 runs daily at
/// daily_at instead. A run with nothing to do costs a few Graph calls; Edge opens only to book. It runs as you, only while you're logged on, needs no admin rights, and runs
/// ev-charger-booking-background.exe directly, so no window appears (the browser runs headless). The same task name as the
/// Python version, so switching replaces the Python task rather than adding a second.</para></summary>
public static class Autostart
{
    public const string TaskName = "ev-charger-booking run";
    public const string ConsoleExe = "ev-charger-booking.exe";
    public const string BackgroundExe = "ev-charger-booking-background.exe";

    public static ITask Create(string name) =>
        new ScheduledTask(name, "Books EV charging when the calendar asks for it, and applies approvals (ev-charger-booking run).");

    /// <summary>The program the task runs (the windowless one beside the running program, if it's there) and its
    /// arguments.</summary>
    public static (string Exe, List<string> Args) Command(string programDir, string configPath)
    {
        var background = Path.Combine(programDir, BackgroundExe);
        var exe = File.Exists(background) ? background : Path.Combine(programDir, ConsoleExe);
        return (exe, ["--config", PyPath.Resolve(configPath), "run", "--hook", "--scheduled"]);
    }

    /// <summary>Every every_minutes (or daily at daily_at when it's 0) and at logon, as a job stopped after 30 minutes,
    /// started now.</summary>
    public static TaskSchedule Schedule(Settings settings) => settings.EveryMinutes > 0
        ? new() { AtLogon = true, Every = TimeSpan.FromMinutes(settings.EveryMinutes), KeepRunning = false }
        : new() { AtLogon = true, DailyAt = settings.DailyAt, KeepRunning = false };

    /// <summary>When it's to run: the scheduled run checks this, so the task can simply repeat.</summary>
    public static bool InHours(Settings settings, DateTime now)
    {
        if (settings.EveryMinutes == 0)
            return true;
        if (settings.Weekdays && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        var time = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return string.CompareOrdinal(time, settings.HoursFrom) >= 0 && string.CompareOrdinal(time, settings.HoursTo) < 0;
    }
}
