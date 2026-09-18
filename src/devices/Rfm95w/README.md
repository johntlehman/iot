# RFM95W LoRa radio (Adafruit 3072, Semtech SX1276)

C# driver for the HopeRF RFM95W (868/915 MHz) LoRa module in point-to-point LoRa mode. No LoRaWAN. Written from the
Semtech SX1276 datasheet; behaviour cross-checked against the MIT-licensed `sandeepmistry/arduino-LoRa` and Adafruit
CircuitPython `rfm9x` libraries. The same driver works on RFM96W/98W (433 MHz) by changing the frequency.

- `Rfm95w/` – `Rfm95w` driver, `LoRaPacket`, parameter enums, register map.
- `samples/Rfm95w.Sample/` – one binary with `tx`, `rx` and `scan` modes; run it on both Pis.
- `tests/Rfm95w.Tests/` – xUnit tests over `FakeSx1276` (register file + FIFO + IRQ model) and `FakeGpioDriver`.

## Wiring (Raspberry Pi SPI0)

| RFM95W breakout | Raspberry Pi | Note |
|---|---|---|
| VIN | 3.3 V | Up to ~120 mA on transmit at 17 dBm, ~140 mA at 20 dBm |
| GND | GND | |
| SCK | GPIO11 (SCLK, pin 23) | |
| MISO | GPIO9 (MISO, pin 21) | |
| MOSI | GPIO10 (MOSI, pin 19) | |
| CS | GPIO8 (CE0, pin 24) | `SpiConnectionSettings(0, 0)` |
| RST | GPIO25 (pin 22) | Active low; any free GPIO |
| G0 (DIO0) | GPIO24 (pin 18) | Rising edge = packet received; any free GPIO, or omit and poll |
| EN | leave open | Pull low to shut the module down |
| ANT | spring antenna / SMA | **Never transmit without an antenna**, the PA can be damaged |

Enable SPI with `sudo raspi-config` → Interface Options → SPI. Check with `ls /dev/spidev0.*`.
The 915 MHz spring antenna (Adafruit 4269) is about 78 mm and should stand straight out from the board.

## Usage

```csharp
using SpiDevice spi = SpiDevice.Create(Rfm95w.GetDefaultSpiSettings(busId: 0, chipSelectLine: 0));
using Rfm95w radio = new(spi, Frequency.FromMegahertz(915), resetPin: 25, dio0Pin: 24);

radio.SpreadingFactor = SpreadingFactor.Sf7;   // defaults: SF7, 125 kHz, CR 4/5, 17 dBm, CRC on, sync 0x12
radio.TxPower = 17;

// Rocket
radio.Send(telemetryBytes);                    // blocks for the time on air, ~41 ms for 10 bytes at SF7/125k

// Ground station
radio.PacketReceived += p => Console.WriteLine($"{p.Payload.Length} bytes, {p.Rssi:F0} dBm, SNR {p.Snr:F1}");
radio.StartReceive();

// Without DIO0 wired:
LoRaPacket? p = radio.Receive(TimeSpan.FromSeconds(1));
```

Both radios must share frequency, spreading factor, bandwidth, coding rate, preamble length and sync word. Payloads
are 1..255 bytes; the driver adds nothing, so the packet format is entirely yours.

## Choosing parameters

| Profile | SF | BW | Time on air, 16 bytes | Notes |
|---|---|---|---|---|
| Fast (default) | 7 | 125 kHz | 51 ms | 5 Hz telemetry with margin; a few km line of sight |
| Balanced | 9 | 125 kHz | 165 ms | 2-3 Hz telemetry, ~6 dB more link budget |
| Long range | 10 | 125 kHz | 330 ms | 1-2 Hz; where 2 km+ altitude and tracking matter more than rate |

`GetTimeOnAir(length)` implements the Semtech AN1200.13 formula so you can size the telemetry interval. The driver sets
LowDataRateOptimize automatically when the symbol time exceeds 16 ms (SF11/SF12 at 125 kHz), as the datasheet requires.
Power above 17 dBm switches on the +20 dBm DAC; keep the duty cycle low there and expect 140 mA peaks.

## Design notes

- Transmit polls `RegIrqFlags` for TxDone rather than using DIO0, so DIO0 is reserved for RxDone and a packet arriving
  while you call `Send` is not lost: `Send` stops receive, transmits, then re-enters continuous receive.
- The DIO0 callback and all public methods share one lock; `PacketReceived` is raised outside it, on the GPIO event thread.
- CRC-failed packets are dropped and counted in `CrcErrorCount`.
- RSSI uses the datasheet correction: packet RSSI = -157 + raw (HF port), plus SNR/4 when the SNR is negative.
- SF6 (implicit header only) is not supported.

## Build and run

```bash
cd src/devices/Rfm95w
dotnet build
DOTNET_ROLL_FORWARD=Major dotnet test        # env var only needed when the .NET 9 runtime is not installed
dotnet publish samples/Rfm95w.Sample -c Release -r linux-arm64 --self-contained false -o out
scp -r out pi@rocket.local:~/lora && scp -r out pi@ground.local:~/lora

ssh pi@rocket.local ~/lora/Rfm95w.Sample scan          # expect 0x42=0x12
ssh pi@ground.local ~/lora/Rfm95w.Sample rx
ssh pi@rocket.local ~/lora/Rfm95w.Sample tx             # ground prints RX #0, #1, ... with RSSI around -40 dBm across a room
```

Then walk the transmitter away and watch RSSI fall and `lost` stay at 0. Add `--sf 10` on both ends for a range test.
