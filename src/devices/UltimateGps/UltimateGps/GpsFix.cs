using Iot.Device.Common;
using Iot.Device.Nmea0183.Sentences;
using UnitsNet;

namespace Iot.Device.UltimateGps
{
    /// <summary>
    /// The latest navigation state, merged from GGA (position, altitude, satellites) and RMC (speed, course, date).
    /// </summary>
    public sealed record GpsFix
    {
        /// <summary>UTC time of the fix. Date comes from RMC; before the first RMC it is the host date.</summary>
        public DateTimeOffset Time { get; init; }

        /// <summary>GGA fix quality.</summary>
        public GpsQuality Quality { get; init; }

        /// <summary>True when the receiver reports a valid position (GGA quality above NoFix, or RMC status 'A').</summary>
        public bool HasFix { get; init; }

        /// <summary>Latitude in decimal degrees, north positive.</summary>
        public double Latitude { get; init; }

        /// <summary>Longitude in decimal degrees, east positive.</summary>
        public double Longitude { get; init; }

        /// <summary>Altitude above mean sea level from GGA. Null until a GGA with altitude has arrived.</summary>
        public Length? Altitude { get; init; }

        /// <summary>Speed over ground from RMC. Null until an RMC has arrived.</summary>
        public Speed? SpeedOverGround { get; init; }

        /// <summary>Course over ground (true) from RMC. Null until an RMC has arrived.</summary>
        public Angle? Course { get; init; }

        /// <summary>Satellites used in the solution, from GGA.</summary>
        public int SatellitesInUse { get; init; }

        /// <summary>Horizontal dilution of precision, from GGA (99 when unknown).</summary>
        public double Hdop { get; init; } = 99;

        /// <summary>The position as a dotnet/iot <see cref="GeographicPosition"/> (height is MSL altitude or 0).</summary>
        public GeographicPosition Position => new(Latitude, Longitude, Altitude?.Meters ?? 0);

        /// <summary>An empty fix, used before any sentence has been received.</summary>
        public static GpsFix Empty { get; } = new GpsFix();

        /// <inheritdoc />
        public override string ToString()
        {
            string alt = Altitude.HasValue ? $"{Altitude.Value.Meters:F1} m" : "n/a";
            string spd = SpeedOverGround.HasValue ? $"{SpeedOverGround.Value.MetersPerSecond:F1} m/s" : "n/a";
            return $"{Time:HH:mm:ss.f} fix={HasFix} ({Quality}) {Latitude:F6},{Longitude:F6} alt={alt} spd={spd} sats={SatellitesInUse} hdop={Hdop:F1}";
        }
    }
}
