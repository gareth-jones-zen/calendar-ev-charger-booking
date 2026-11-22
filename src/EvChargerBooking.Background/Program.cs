// calendar-ev-charger-booking-background.exe: no window, ever. What the scheduled task runs (`--config SETTINGS run --hook
// --scheduled`); anything else runs as the console program would, with nothing shown.
return EvChargerBooking.Cli.Run(args, new EvChargerBooking.CliHost { Out = TextWriter.Null, Err = TextWriter.Null });
