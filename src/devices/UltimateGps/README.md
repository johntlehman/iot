# Adafruit Ultimate GPS (PA1616S / MTK3339, product 746)

C# driver for the Adafruit Ultimate GPS breakout. The module talks NMEA 0183 over a 3.3 V UART and is configured with
MediaTek PMTK commands. Parsing reuses `Iot.Device.Nmea0183` from the `Iot.Device.Bindings` NuGet package; this driver
adds the serial reader, PMTK command builder with acknowledgement tracking, and a merged `GpsFix` record.

- `UltimateGps/` – `UltimateGps` driver, `PmtkCommand`, `PmtkAck`, `NmeaOutput`, `GpsFix`.
- `samples/UltimateGps.Sample/` – Raspberry Pi console tool: firmware query, flight configuration, fix stream.
- `tests/UltimateGps.Tests/` – xUnit tests over an in-memory `FakeSerialStream`.

## Wiring

| GPS breakout | Raspberry Pi | Note |
|---|---|---|
| VIN | 3.3 V or 5 V | On-board regulator, either is fine |
| GND | GND | |
| TX | GPIO15 (RXD, pin 10) | GPS transmits NMEA to the Pi |
| RX | GPIO14 (TXD, pin 8) | Pi sends PMTK commands |
| EN | (optional GPIO) | Pull low to switch the module off |
| FIX | (optional GPIO) | Pulses once per second without a fix, once every 15 s with one |
| PPS | (optional GPIO) | 1 pulse per second, on the second, when fixed |
| VBAT / CR1220 | | Backup battery keeps ephemeris for fast warm starts |

Free the Pi UART first: `sudo raspi-config` → Interface Options → Serial Port → login shell **No**, serial hardware
**Yes**, then reboot. The port is `/dev/serial0`. On a Pi 3/4/Zero W move Bluetooth off the PL011 with
`dtoverlay=disable-bt` in `/boot/firmware/config.txt` so the UART clock is stable at 57600 baud.

## Usage

```csharp
using UltimateGps gps = UltimateGps.Create("/dev/serial0");   // 9600 baud, factory default
gps.FixUpdated += fix => Console.WriteLine(fix);
gps.Start();

string firmware = await gps.QueryFirmwareAsync(TimeSpan.FromSeconds(2));
await gps.ConfigureForFlightAsync();   // 57600 baud, RMC+GGA, 5 Hz fix, 10 Hz output, each step acknowledged

GpsFix latest = gps.LastFix;           // Latitude/Longitude (deg), Altitude (MSL), SpeedOverGround, Course, ...
```

`FixUpdated` fires once per navigation epoch on the reader thread, after both GGA and RMC for that timestamp have
arrived (or after either one when the other sentence type is disabled). `GpsFix.Altitude` is the GGA mean-sea-level
altitude, which is what you want to compare with the barometric altitude.

## Rates

The MTK3339 computes a position at most **5 times per second** (`PMTK300`, minimum 200 ms). `PMTK220` can push NMEA
output to 10 Hz, which repeats or interpolates between fixes. `ConfigureForFlightAsync` uses 5 Hz fix / 10 Hz output
with only RMC and GGA enabled. Both sentences at 10 Hz need roughly 1500 bytes/s, which does not fit in 9600 baud, so
the baud rate is raised to 57600 first.

The module does not acknowledge `PMTK251` (baud change) at the old rate; `SetBaudRateAsync` sends it, waits 100 ms,
then switches the host port. Settings persist while the module has power or a backup battery, so a later run may find
the module already at 57600. The sample handles this by retrying the firmware query at the flight rate.

## PMTK commands

| Factory | Sentence | Purpose |
|---|---|---|
| `SetNmeaUpdateRate(hz)` | PMTK220 | NMEA output interval, 1..10 Hz |
| `SetFixRate(hz)` | PMTK300 | Position fix interval, 1..5 Hz |
| `SetBaudRate(baud)` | PMTK251 | 4800..115200 |
| `SetNmeaOutput(NmeaOutput)` | PMTK314 | Which of GLL/RMC/VTG/GGA/GSA/GSV to emit |
| `EnableSbas()` / `EnableWaas()` | PMTK313 / PMTK301 | Differential corrections (1 Hz only) |
| `Standby()` / `Wake()` | PMTK161 / PMTK010 | Low-power mode |
| `QueryFirmware()` | PMTK605 | Answered by PMTK705 |
| `FullColdStart()` | PMTK104 | Clear everything |
| `AntennaStatus(bool)` | PGCMD,33 | `$PGTOP` antenna messages |

Every command is acknowledged with `$PMTK001,<type>,<flag>`; `PmtkAck` parses it and `SendCommandAsync` matches it
to the pending command by type.

## Build and run

```bash
cd src/devices/UltimateGps
dotnet build
DOTNET_ROLL_FORWARD=Major dotnet test        # env var only needed when the .NET 9 runtime is not installed
dotnet publish samples/UltimateGps.Sample -c Release -r linux-arm64 --self-contained false -o out
scp -r out pi@rocket.local:~/gps
ssh pi@rocket.local ~/gps/UltimateGps.Sample /dev/serial0          # add --raw to echo every NMEA line
```

Outdoors with a clear sky, the first fix takes under a minute cold and a few seconds warm. Once configured, the
printed `dt` between fixes should be about 100 ms.
