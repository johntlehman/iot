using System.Globalization;
using System.Text;

namespace Iot.Device.UltimateGps
{
    /// <summary>
    /// A MediaTek PMTK (or Globaltop PGCMD) command sentence for the MTK3339.
    /// Reference: Adafruit PMTK_A11 command sheet and Adafruit_GPS/Adafruit_PMTK.h.
    /// </summary>
    public sealed class PmtkCommand
    {
        private PmtkCommand(int type, string prefix, params string[] args)
        {
            Type = type;
            Prefix = prefix;
            Arguments = args;
        }

        /// <summary>Packet type, e.g. 220. The module echoes it in the PMTK001 acknowledgement.</summary>
        public int Type { get; }

        /// <summary>"PMTK" or "PGCMD".</summary>
        public string Prefix { get; }

        /// <summary>Comma-separated arguments after the type.</summary>
        public IReadOnlyList<string> Arguments { get; }

        /// <summary>
        /// The body between '$' and '*', e.g. "PMTK220,100".
        /// </summary>
        public string Body
        {
            get
            {
                StringBuilder sb = new(Prefix);
                if (Prefix == "PGCMD")
                {
                    sb.Append(',').Append(Type.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(Type.ToString("D3", CultureInfo.InvariantCulture));
                }

                foreach (string arg in Arguments)
                {
                    sb.Append(',').Append(arg);
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// The full sentence without line terminator, e.g. "$PMTK220,100*2F".
        /// </summary>
        public string Sentence => $"${Body}*{Checksum(Body):X2}";

        /// <summary>
        /// The sentence with the CR LF terminator the module expects on the wire.
        /// </summary>
        public string ToWireString() => Sentence + "\r\n";

        /// <inheritdoc />
        public override string ToString() => Sentence;

        /// <summary>
        /// NMEA checksum: XOR of every character between '$' and '*'.
        /// </summary>
        public static byte Checksum(string body)
        {
            byte checksum = 0;
            foreach (char c in body)
            {
                checksum ^= (byte)c;
            }

            return checksum;
        }

        /// <summary>
        /// PMTK220: interval between NMEA output bursts. 1..10 Hz (10 Hz needs a higher baud rate and RMC+GGA only).
        /// </summary>
        public static PmtkCommand SetNmeaUpdateRate(int hertz)
        {
            if (hertz < 1 || hertz > 10)
            {
                throw new ArgumentOutOfRangeException(nameof(hertz), hertz, "NMEA update rate must be 1..10 Hz");
            }

            return SetNmeaUpdateInterval(1000 / hertz);
        }

        /// <summary>
        /// PMTK220 with an explicit interval, 100..10000 ms.
        /// </summary>
        public static PmtkCommand SetNmeaUpdateInterval(int milliseconds)
        {
            if (milliseconds < 100 || milliseconds > 10000)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "NMEA update interval must be 100..10000 ms");
            }

            return new PmtkCommand(220, "PMTK", Invariant(milliseconds));
        }

        /// <summary>
        /// PMTK300: position fix interval. The MTK3339 fixes at most 5 times a second, so 1..5 Hz.
        /// </summary>
        public static PmtkCommand SetFixRate(int hertz)
        {
            if (hertz < 1 || hertz > 5)
            {
                throw new ArgumentOutOfRangeException(nameof(hertz), hertz, "Fix rate must be 1..5 Hz");
            }

            return SetFixInterval(1000 / hertz);
        }

        /// <summary>
        /// PMTK300 with an explicit interval, 200..10000 ms.
        /// </summary>
        public static PmtkCommand SetFixInterval(int milliseconds)
        {
            if (milliseconds < 200 || milliseconds > 10000)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds), milliseconds, "Fix interval must be 200..10000 ms");
            }

            return new PmtkCommand(300, "PMTK", Invariant(milliseconds), "0", "0", "0", "0");
        }

        /// <summary>
        /// PMTK251: serial baud rate. The module switches immediately and does not acknowledge at the old rate.
        /// </summary>
        public static PmtkCommand SetBaudRate(int baud)
        {
            if (baud is not (4800 or 9600 or 14400 or 19200 or 38400 or 57600 or 115200))
            {
                throw new ArgumentOutOfRangeException(nameof(baud), baud, "Unsupported baud rate");
            }

            return new PmtkCommand(251, "PMTK", Invariant(baud));
        }

        /// <summary>
        /// PMTK314: which NMEA sentences to output, each once per fix.
        /// </summary>
        public static PmtkCommand SetNmeaOutput(NmeaOutput output)
        {
            // 19 fields: GLL, RMC, VTG, GGA, GSA, GSV, then 13 reserved/unused.
            string[] fields = new string[19];
            fields[0] = output.HasFlag(NmeaOutput.Gll) ? "1" : "0";
            fields[1] = output.HasFlag(NmeaOutput.Rmc) ? "1" : "0";
            fields[2] = output.HasFlag(NmeaOutput.Vtg) ? "1" : "0";
            fields[3] = output.HasFlag(NmeaOutput.Gga) ? "1" : "0";
            fields[4] = output.HasFlag(NmeaOutput.Gsa) ? "1" : "0";
            fields[5] = output.HasFlag(NmeaOutput.Gsv) ? "1" : "0";
            for (int i = 6; i < fields.Length; i++)
            {
                fields[i] = "0";
            }

            return new PmtkCommand(314, "PMTK", fields);
        }

        /// <summary>PMTK313: search for SBAS satellites (only effective at 1 Hz).</summary>
        public static PmtkCommand EnableSbas(bool enable = true) => new(313, "PMTK", enable ? "1" : "0");

        /// <summary>PMTK301: use WAAS for DGPS corrections.</summary>
        public static PmtkCommand EnableWaas() => new(301, "PMTK", "2");

        /// <summary>PMTK161: enter standby. Send any byte to wake.</summary>
        public static PmtkCommand Standby() => new(161, "PMTK", "0");

        /// <summary>PMTK010,002: the wake-up message Adafruit sends to leave standby.</summary>
        public static PmtkCommand Wake() => new(10, "PMTK", "002");

        /// <summary>PMTK605: query firmware release. The module answers with PMTK705.</summary>
        public static PmtkCommand QueryFirmware() => new(605, "PMTK");

        /// <summary>PMTK104: full cold start, clears all stored data.</summary>
        public static PmtkCommand FullColdStart() => new(104, "PMTK");

        /// <summary>PGCMD,33: antenna status messages ($PGTOP) on or off.</summary>
        public static PmtkCommand AntennaStatus(bool enable) => new(33, "PGCMD", enable ? "1" : "0");

        private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
