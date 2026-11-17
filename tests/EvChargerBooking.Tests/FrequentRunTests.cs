namespace EvChargerBooking.Tests;

/// <summary>2.1.0: runs every 15 minutes in working hours, each question notified once, failures on the third.</summary>
public sealed class FrequentRunTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ev-frequent-" + Guid.NewGuid().ToString("N"));

    public FrequentRunTests() => Directory.CreateDirectory(dir);

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

    private Settings Parse(string text = "") =>
        Settings.Parse($"state_file = \"{dir.Replace('\\', '/')}/state.json\"\n" + text, Path.Combine(dir, "ev.toml"), _ => null);

    [Fact]
    public void Every_fifteen_minutes_in_working_hours_by_default()
    {
        var s = Parse();
        Assert.Equal((15, "07:00", "19:00", true), (s.EveryMinutes, s.HoursFrom, s.HoursTo, s.Weekdays));
        var schedule = Autostart.Schedule(s);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Every);
        Assert.Null(schedule.DailyAt);
        Assert.True(schedule.AtLogon);
        Assert.True(Autostart.InHours(s, new DateTime(2026, 11, 17, 7, 0, 0)));     // a Tuesday
        Assert.False(Autostart.InHours(s, new DateTime(2026, 11, 17, 19, 0, 0)));
        Assert.False(Autostart.InHours(s, new DateTime(2026, 11, 17, 6, 59, 0)));
        Assert.False(Autostart.InHours(s, new DateTime(2026, 11, 21, 12, 0, 0)));   // a Saturday
    }

    [Fact]
    public void Every_minutes_zero_is_the_old_daily_run()
    {
        var s = Parse("every_minutes = 0\ndaily_at = \"08:00\"\n");
        var schedule = Autostart.Schedule(s);
        Assert.Equal("08:00", schedule.DailyAt);
        Assert.Null(schedule.Every);
        Assert.True(Autostart.InHours(s, new DateTime(2026, 11, 21, 23, 0, 0)));   // a scheduled run always runs
    }

    [Fact]
    public void Hours_and_weekdays_are_settings()
    {
        var s = Parse("every_minutes = 30\nhours = \"08:00-18:30\"\nweekdays = false\n");
        Assert.Equal((30, "08:00", "18:30", false), (s.EveryMinutes, s.HoursFrom, s.HoursTo, s.Weekdays));
        Assert.True(Autostart.InHours(s, new DateTime(2026, 11, 21, 12, 0, 0)));
        Assert.Throws<PyValueError>(() => Parse("every_minutes = 2\n"));
        Assert.Throws<PyValueError>(() => Parse("hours = \"all day\"\n"));
    }

    [Fact]
    public void Each_question_is_notified_once_until_it_goes_away()
    {
        var s = Parse();
        var now = new DateTime(2026, 11, 17, 9, 0, 0);
        Assert.Equal(["Wed 18 Nov: book?"], Notices.NewQuestions(s, ["Wed 18 Nov: book?"], now));
        Assert.Empty(Notices.NewQuestions(s, ["Wed 18 Nov: book?"], now));
        Assert.Equal(["Thu 19 Nov: declined"], Notices.NewQuestions(s, ["Wed 18 Nov: book?", "Thu 19 Nov: declined"], now));
        Assert.Empty(Notices.NewQuestions(s, [], now));                                                 // both answered
        Assert.Equal(["Wed 18 Nov: book?"], Notices.NewQuestions(s, ["Wed 18 Nov: book?"], now));     // back again: told again
    }

    [Fact]
    public void A_failure_is_notified_on_the_third_in_a_row_and_a_sign_in_on_the_first()
    {
        var s = Parse();
        Assert.False(Notices.Failed(s, signIn: false));
        Assert.False(Notices.Failed(s, signIn: false));
        Assert.True(Notices.Failed(s, signIn: false));
        Assert.False(Notices.Failed(s, signIn: false));   // not again until a run works
        Notices.NewQuestions(s, [], DateTime.Now);        // a run that worked
        Assert.True(Notices.Failed(s, signIn: true));
        Assert.False(Notices.Failed(s, signIn: true));
        Assert.EndsWith("state.notified.json", Notices.PathFor(s));
    }
}
