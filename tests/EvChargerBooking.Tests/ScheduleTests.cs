using WinBackground;

namespace EvChargerBooking.Tests;

/// <summary>[schedule]: the library's shared scheduling settings, with the old top-level keys still read.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ev-schedule-" + Guid.NewGuid().ToString("N"));

    public ScheduleTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Head => $"state_file = \"{dir.Replace('\\', '/')}/state.json\"\nlog_file = \"{dir.Replace('\\', '/')}/ev.log\"\n";

    private Settings Parse(string text = "") => Settings.Parse(Head + text, Path.Combine(dir, "ev.toml"), _ => null);

    /// <summary>A task that keeps what it was registered with, description (and so the fingerprint) included.</summary>
    private sealed class RecordingTask(string name) : ITask
    {
        public string Name { get; } = name;
        public TaskSchedule? Registered;

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);

        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Registered = schedule;
            return exe;
        }

        public bool Disable() => true;
        public bool Stop() => true;

        public IReadOnlyDictionary<string, string>? Status() => Registered is null ? null : new Dictionary<string, string>
        {
            ["state"] = "Ready",
            ["description"] = Registered.Fingerprint is { } fp ? ScheduleDrift.WithFingerprint("desc", fp) : "desc",
        };
    }

    private (int Code, string Out) Autostart(string settingsText, RecordingTask task, string action)
    {
        var path = Path.Combine(dir, "ev.toml");
        File.WriteAllText(path, Head + settingsText);
        var output = new StringWriter();
        var code = Cli.Run(["--config", path, "autostart", action], new CliHost { Out = output, Err = output, Tasks = _ => task, Env = _ => null });
        return (code, output.ToString());
    }

    [Fact]
    public void Without_schedule_settings_the_defaults_are_unchanged()
    {
        var s = Parse();
        Assert.False(s.ScheduleFromOldKeys);
        var task = EvChargerBooking.Autostart.Schedule(s);
        Assert.Equal((true, TimeSpan.FromMinutes(15), (string?)null, (TimeSpan?)null), (task.AtLogon, task.Every, task.DailyAt, task.AfterUnlock));
        Assert.Equal("v1 job every=15 daily=- logon=yes unlock=0", task.Fingerprint);
        Assert.False(EvChargerBooking.Autostart.InHours(s, new DateTime(2026, 11, 21, 12, 0, 0)));   // a Saturday
    }

    [Fact]
    public void A_schedule_table_is_read()
    {
        var s = Parse("[schedule]\nhours = \"00:00-23:59\"\ndays = \"Mon-Sun\"\n");
        Assert.Equal((15, "00:00", "23:59", false), (s.EveryMinutes, s.HoursFrom, s.HoursTo, s.Weekdays));
        Assert.False(s.ScheduleFromOldKeys);
        Assert.True(EvChargerBooking.Autostart.InHours(s, new DateTime(2026, 11, 21, 12, 0, 0)));

        var daily = Parse("[schedule]\nevery_minutes = 0\ndaily_at = \"08:00\"\n");
        Assert.Equal(("08:00", (TimeSpan?)null), (EvChargerBooking.Autostart.Schedule(daily).DailyAt, EvChargerBooking.Autostart.Schedule(daily).Every));
        Assert.Throws<PyValueError>(() => Parse("[schedule]\nevery_minutes = 2\n"));
        Assert.Throws<PyValueError>(() => Parse("[schedule]\nevery_minute = 15\n"));
    }

    [Fact]
    public void The_old_top_level_keys_still_work_and_are_noted()
    {
        var s = Parse("daily_at = \"08:00\"\nhours = \"00:00-23:59\"\nweekdays = false\n");
        Assert.True(s.ScheduleFromOldKeys);
        Assert.Equal("v1 job every=15 daily=- logon=yes unlock=0", EvChargerBooking.Autostart.Schedule(s).Fingerprint);
        Assert.True(EvChargerBooking.Autostart.InHours(s, new DateTime(2026, 11, 21, 23, 0, 0)));
        Assert.Contains("[schedule]", EvChargerBooking.Autostart.OldScheduleNote);
    }

    [Fact]
    public void Both_forms_at_once_is_an_error()
    {
        var error = Assert.Throws<PyValueError>(() => Parse("weekdays = false\n[schedule]\nevery_minutes = 30\n"));
        Assert.Contains("[schedule]", error.Message);
        Assert.Contains("weekdays", error.Message);
    }

    [Fact]
    public void Autostart_records_the_schedule_and_status_spots_a_change()
    {
        var task = new RecordingTask(EvChargerBooking.Autostart.TaskName);
        var (code, output) = Autostart("[schedule]\nhours = \"00:00-23:59\"\ndays = \"Mon-Sun\"\n", task, "on");
        Assert.Equal(0, code);
        Assert.Contains("runs every 15 minutes, 00:00-23:59 and at logon, and has started now", output);
        Assert.DoesNotContain("settings changed", Autostart("[schedule]\nhours = \"00:00-23:59\"\ndays = \"Mon-Sun\"\n", task, "status").Out);
        Assert.Contains("settings changed since this task was registered: run `calendar-ev-charger-booking autostart on`",
            Autostart("[schedule]\nevery_minutes = 30\nhours = \"00:00-23:59\"\n", task, "status").Out);
    }
}
