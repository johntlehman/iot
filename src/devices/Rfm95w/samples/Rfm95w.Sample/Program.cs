using System.Buffers.Binary;
using System.Device.Spi;
using System.Diagnostics;
using System.Text;
using Iot.Device.Rfm95w;
using UnitsNet;

// Usage:
//   Rfm95w.Sample tx   [options]   send a counter + fake telemetry once a second
//   Rfm95w.Sample rx   [options]   print every packet with RSSI/SNR and loss count
//   Rfm95w.Sample scan [options]   dump registers 0x00..0x4D (wiring check)
// Options: --freq 915 --sf 7 --bw 125 --cr 5 --power 17 --reset 25 --dio0 24 --cs 0 --bus 0 --interval 1000

Console.WriteLine("=== RFM95W LoRa Diagnostic Tool ===");

if (args.Length == 0 || args[0] is not ("tx" or "rx" or "scan"))
{
    Console.WriteLine("Usage: Rfm95w.Sample tx|rx|scan [--freq MHz] [--sf 7..12] [--bw 125|250|500|...] [--cr 5..8] [--power 2..20] [--reset pin] [--dio0 pin|-1] [--bus 0] [--cs 0] [--interval ms]");
    return 1;
}

string mode = args[0];
double freqMhz = Option("--freq", 915.0);
int sf = (int)Option("--sf", 7);
int bwKhz = (int)Option("--bw", 125);
int cr = (int)Option("--cr", 5);
int power = (int)Option("--power", 17);
int resetPin = (int)Option("--reset", 25);
int dio0Pin = (int)Option("--dio0", 24);
int bus = (int)Option("--bus", 0);
int cs = (int)Option("--cs", 0);
int intervalMs = (int)Option("--interval", 1000);

using SpiDevice spi = SpiDevice.Create(Rfm95w.GetDefaultSpiSettings(bus, cs));

if (mode == "scan")
{
    // No driver: just read registers so a wiring fault is visible even if the version check would fail.
    Console.WriteLine("Register dump (LoRa mode not yet selected):");
    for (byte reg = 0x00; reg <= 0x4D; reg++)
    {
        Span<byte> w = stackalloc byte[2] { reg, 0 };
        Span<byte> r = stackalloc byte[2];
        spi.TransferFullDuplex(w, r);
        Console.Write($"0x{reg:X2}=0x{r[1]:X2}  ");
        if ((reg + 1) % 8 == 0)
        {
            Console.WriteLine();
        }
    }

    Console.WriteLine();
    Console.WriteLine("RegVersion (0x42) must read 0x12. All 0x00 or 0xFF means MISO/MOSI/CS/power are wrong.");
    return 0;
}

Rfm95w radio;
try
{
    radio = new Rfm95w(spi, Frequency.FromMegahertz(freqMhz), resetPin, dio0Pin);
    Console.WriteLine($"RFM95W detected (version 0x{radio.Version:X2}).");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED to initialise RFM95W: {ex.Message}");
    return 1;
}

using (radio)
{
    radio.SpreadingFactor = (SpreadingFactor)sf;
    radio.Bandwidth = bwKhz switch
    {
        500 => LoRaBandwidth.Bw500kHz,
        250 => LoRaBandwidth.Bw250kHz,
        125 => LoRaBandwidth.Bw125kHz,
        62 => LoRaBandwidth.Bw62_5kHz,
        41 => LoRaBandwidth.Bw41_7kHz,
        31 => LoRaBandwidth.Bw31_25kHz,
        20 => LoRaBandwidth.Bw20_8kHz,
        15 => LoRaBandwidth.Bw15_6kHz,
        10 => LoRaBandwidth.Bw10_4kHz,
        7 => LoRaBandwidth.Bw7_8kHz,
        _ => throw new ArgumentException($"Unsupported bandwidth {bwKhz} kHz"),
    };
    radio.CodingRate = (CodingRate)(cr - 4);
    radio.TxPower = power;

    Console.WriteLine($"{freqMhz} MHz, SF{sf}, {bwKhz} kHz, CR 4/{cr}, {power} dBm, LDRO={radio.LowDataRateOptimizeEnabled}, sync 0x{radio.SyncWord:X2}");
    Console.WriteLine($"Time on air for a 16-byte packet: {radio.GetTimeOnAir(16).TotalMilliseconds:F1} ms");
    Console.WriteLine("NEVER transmit without an antenna attached.");
    Console.WriteLine();

    if (mode == "tx")
    {
        uint counter = 0;
        Stopwatch uptime = Stopwatch.StartNew();
        while (true)
        {
            // Fake telemetry: counter, uptime ms, "altitude" and "temperature" so the receiver has something to decode.
            byte[] packet = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, counter);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)uptime.ElapsedMilliseconds);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8), 123.4f + counter);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12), 21.5f);

            Stopwatch sw = Stopwatch.StartNew();
            radio.Send(packet);
            Console.WriteLine($"TX #{counter} ({packet.Length} bytes) in {sw.Elapsed.TotalMilliseconds:F1} ms");

            counter++;
            Thread.Sleep(intervalMs);
        }
    }

    // rx
    uint expected = 0;
    int received = 0;
    int lost = 0;

    radio.PacketReceived += p =>
    {
        received++;
        string decoded;
        if (p.Payload.Length >= 16)
        {
            uint counter = BinaryPrimitives.ReadUInt32LittleEndian(p.Payload);
            uint up = BinaryPrimitives.ReadUInt32LittleEndian(p.Payload.AsSpan(4));
            float alt = BinaryPrimitives.ReadSingleLittleEndian(p.Payload.AsSpan(8));
            float temp = BinaryPrimitives.ReadSingleLittleEndian(p.Payload.AsSpan(12));
            if (received > 1 && counter > expected)
            {
                lost += (int)(counter - expected);
            }

            expected = counter + 1;
            decoded = $"#{counter} up={up} ms alt={alt:F1} temp={temp:F1}";
        }
        else
        {
            decoded = Encoding.ASCII.GetString(p.Payload);
        }

        Console.WriteLine($"RX {p}  {decoded}  crcErr={radio.CrcErrorCount} lost={lost}");
    };

    if (dio0Pin >= 0)
    {
        radio.StartReceive();
        Console.WriteLine("Listening (DIO0 interrupt). Ctrl+C to stop.");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    Console.WriteLine("Listening (polling, no DIO0). Ctrl+C to stop.");
    while (true)
    {
        LoRaPacket? p = radio.Receive(TimeSpan.FromSeconds(5));
        if (p == null)
        {
            Console.WriteLine($"  ... nothing for 5 s (noise floor {radio.Rssi:F0} dBm)");
            continue;
        }

        received++;
        Console.WriteLine($"RX {p}  {Encoding.ASCII.GetString(p.Payload)}");
    }
}

double Option(string name, double fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
