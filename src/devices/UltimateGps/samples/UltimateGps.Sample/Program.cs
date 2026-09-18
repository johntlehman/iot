using System.Diagnostics;
using Iot.Device.UltimateGps;

Console.WriteLine("=== Ultimate GPS (PA1616S) Diagnostic Tool ===");

// Raspberry Pi: /dev/serial0 with the serial console disabled (raspi-config -> Interface Options -> Serial Port).
string portName = args.Length > 0 ? args[0] : "/dev/serial0";
bool raw = args.Contains("--raw");

using UltimateGps gps = UltimateGps.Create(portName, UltimateGps.DefaultBaudRate);
if (raw)
{
    gps.SentenceReceived += line => Console.WriteLine($"  < {line}");
}

gps.ParserError += (line, error) => Console.WriteLine($"  ! {error}: {line}");
gps.Start();

// The module may still be at 57600 from a previous run; if nothing parses at 9600, try the flight rate.
Console.WriteLine($"Opened {portName} at {UltimateGps.DefaultBaudRate} baud. Waiting for the module...");
Thread.Sleep(1500);

try
{
    string release = await gps.QueryFirmwareAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Firmware: {release}");
}
catch (TimeoutException)
{
    Console.WriteLine("No firmware response at 9600 baud. Trying 57600...");
    await gps.SetBaudRateAsync(UltimateGps.FlightBaudRate);
    string release = await gps.QueryFirmwareAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine($"Firmware: {release}");
}

Console.WriteLine("Configuring for flight: 57600 baud, RMC+GGA, 5 Hz fix, 10 Hz output...");
await gps.ConfigureForFlightAsync();
Console.WriteLine("Configured.");

Stopwatch sinceStart = Stopwatch.StartNew();
bool hadFix = false;
DateTimeOffset lastTime = DateTimeOffset.MinValue;

gps.FixUpdated += fix =>
{
    double dt = lastTime == DateTimeOffset.MinValue ? 0 : (fix.Time - lastTime).TotalMilliseconds;
    lastTime = fix.Time;

    if (fix.HasFix && !hadFix)
    {
        hadFix = true;
        Console.WriteLine($"*** First fix after {sinceStart.Elapsed.TotalSeconds:F1} s ***");
    }

    Console.WriteLine($"{fix}  dt={dt:F0} ms");
};

Console.WriteLine("Streaming fixes. Ctrl+C to stop. Expect dt ~100 ms outdoors once configured.");
await Task.Delay(Timeout.Infinite);
