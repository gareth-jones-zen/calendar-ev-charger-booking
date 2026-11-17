// ev-charger-booking.exe: every command, with output. Output is Unicode to a console and UTF-8 when redirected (see
// ConsoleText).
var output = WinBackground.ConsoleText.Out;
return EvChargerBooking.Cli.Run(args, new EvChargerBooking.CliHost
{
    Out = output,
    Err = WinBackground.ConsoleText.Error,
    LogEcho = output,
});
