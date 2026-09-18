namespace Iot.Device.UltimateGps
{
    /// <summary>
    /// NMEA sentences the module emits, for PMTK314. Combine with bitwise OR.
    /// </summary>
    [Flags]
    public enum NmeaOutput
    {
        /// <summary>No sentences.</summary>
        None = 0,

        /// <summary>GLL: geographic position, latitude / longitude.</summary>
        Gll = 1 << 0,

        /// <summary>RMC: recommended minimum (position, speed, course, date/time).</summary>
        Rmc = 1 << 1,

        /// <summary>VTG: course and speed over ground.</summary>
        Vtg = 1 << 2,

        /// <summary>GGA: fix data (position, altitude, satellites, HDOP).</summary>
        Gga = 1 << 3,

        /// <summary>GSA: DOP and active satellites.</summary>
        Gsa = 1 << 4,

        /// <summary>GSV: satellites in view.</summary>
        Gsv = 1 << 5,

        /// <summary>RMC and GGA: everything a flight computer needs, small enough for 10 Hz.</summary>
        RmcGga = Rmc | Gga,

        /// <summary>RMC, GGA and GSA.</summary>
        RmcGgaGsa = Rmc | Gga | Gsa,

        /// <summary>All six standard sentences.</summary>
        All = Gll | Rmc | Vtg | Gga | Gsa | Gsv,
    }
}
