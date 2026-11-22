using PyCompat;

namespace EvChargerBooking.Tests;

/// <summary>1.2.0: a base settings file, time_zone, and the car's details in [car].</summary>
public sealed class BaseSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ev-base-" + Guid.NewGuid().ToString("N"));

    public BaseSettingsTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "base-config.toml"), """
            login_hint = "me@example.com"
            tenant = "contoso"
            msal_cache = "%ROOT%/msal-cache.json"
            time_zone = "W. Europe Standard Time"
            unrelated = "ignored"

            [paths]
            ROOT = "C:/tools"
            EDGE_M365 = "%ROOT%/edge"

            [review]
            file = "%ROOT%/pending.json"
            lock = "pending-lock"
            """);
    }

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

    private Settings Parse(string text) => Settings.Parse(text, Path.Combine(dir, "calendar-ev-charger-booking.toml"), _ => null);

    [Fact]
    public void Shared_settings_and_paths_come_from_the_base()
    {
        var s = Parse("""
            base = "base-config.toml"
            state_file = "%ROOT%/state.json"
            edge_profile = "%EDGE_M365%"

            [review]
            source = "ev"
            """);
        Assert.Equal(("me@example.com", "contoso", "W. Europe Standard Time"), (s.LoginHint, s.Tenant, s.Timezone));
        Assert.Equal(PyPath.Pure("C:/tools/msal-cache.json"), s.MsalCache);
        Assert.Equal(PyPath.Pure("C:/tools/state.json"), s.StateFile);
        Assert.Equal(PyPath.Pure("C:/tools/edge"), s.EdgeProfile);
        Assert.NotNull(s.Review);
    }

    [Fact]
    public void The_tools_own_timezone_wins_over_the_bases()
    {
        Assert.Equal("GMT Standard Time", Parse("base = \"base-config.toml\"\ntimezone = \"GMT Standard Time\"\n").Timezone);
        Assert.Equal("GMT Standard Time", Parse("").Timezone);
    }

    [Fact]
    public void Car_details_can_be_in_the_settings()
    {
        var s = Parse("""
            [car]
            make = "Tesla"
            model = "Model 3"
            registration = "AB12 CDE"
            additional_information = ""
            """);
        var car = Form.Car(s);
        Assert.Equal(("Tesla", "Model 3", "AB12 CDE", ""),
            ((string)car["car_make"]!, (string)car["car_model"]!, (string)car["car_registration"]!, (string)car["additional_information"]!));
        Assert.Throws<PyValueError>(() => Parse("[car]\nmake = 3\n"));
    }

    [Fact]
    public void A_missing_base_is_a_settings_error()
    {
        Assert.Contains("doesn't exist", Assert.Throws<PyValueError>(() => Parse("base = \"nope.toml\"\n")).Message);
    }
}
