using Iot.Device.UltimateGps;
using Xunit;

namespace UltimateGps.Tests
{
    /// <summary>
    /// Expected strings come from Adafruit_GPS/src/Adafruit_PMTK.h.
    /// </summary>
    public class PmtkCommandTests
    {
        [Theory]
        [InlineData(1, "$PMTK220,1000*1F")]
        [InlineData(2, "$PMTK220,500*2B")]
        [InlineData(5, "$PMTK220,200*2C")]
        [InlineData(10, "$PMTK220,100*2F")]
        public void SetNmeaUpdateRate_MatchesAdafruit(int hz, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetNmeaUpdateRate(hz).Sentence);
        }

        [Theory]
        [InlineData(10000, "$PMTK220,10000*2F")]
        [InlineData(5000, "$PMTK220,5000*1B")]
        public void SetNmeaUpdateInterval_MatchesAdafruit(int ms, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetNmeaUpdateInterval(ms).Sentence);
        }

        [Theory]
        [InlineData(1, "$PMTK300,1000,0,0,0,0*1C")]
        [InlineData(5, "$PMTK300,200,0,0,0,0*2F")]
        public void SetFixRate_MatchesAdafruit(int hz, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetFixRate(hz).Sentence);
        }

        [Theory]
        [InlineData(10000, "$PMTK300,10000,0,0,0,0*2C")]
        [InlineData(5000, "$PMTK300,5000,0,0,0,0*18")]
        public void SetFixInterval_MatchesAdafruit(int ms, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetFixInterval(ms).Sentence);
        }

        [Theory]
        [InlineData(9600, "$PMTK251,9600*17")]
        [InlineData(57600, "$PMTK251,57600*2C")]
        [InlineData(115200, "$PMTK251,115200*1F")]
        public void SetBaudRate_MatchesAdafruit(int baud, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetBaudRate(baud).Sentence);
        }

        [Theory]
        [InlineData(NmeaOutput.Gll, "$PMTK314,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.Rmc, "$PMTK314,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.Vtg, "$PMTK314,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.Gga, "$PMTK314,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.Gsa, "$PMTK314,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.Gsv, "$PMTK314,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.RmcGga, "$PMTK314,0,1,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*28")]
        [InlineData(NmeaOutput.RmcGgaGsa, "$PMTK314,0,1,0,1,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0*29")]
        [InlineData(NmeaOutput.All, "$PMTK314,1,1,1,1,1,1,0,0,0,0,0,0,0,0,0,0,0,0,0*28")]
        [InlineData(NmeaOutput.None, "$PMTK314,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*28")]
        public void SetNmeaOutput_MatchesAdafruit(NmeaOutput output, string expected)
        {
            Assert.Equal(expected, PmtkCommand.SetNmeaOutput(output).Sentence);
        }

        [Fact]
        public void MiscCommands_MatchAdafruit()
        {
            Assert.Equal("$PMTK313,1*2E", PmtkCommand.EnableSbas().Sentence);
            Assert.Equal("$PMTK301,2*2E", PmtkCommand.EnableWaas().Sentence);
            Assert.Equal("$PMTK161,0*28", PmtkCommand.Standby().Sentence);
            Assert.Equal("$PMTK010,002*2D", PmtkCommand.Wake().Sentence);
            Assert.Equal("$PMTK605*31", PmtkCommand.QueryFirmware().Sentence);
            Assert.Equal("$PGCMD,33,1*6C", PmtkCommand.AntennaStatus(true).Sentence);
            Assert.Equal("$PGCMD,33,0*6D", PmtkCommand.AntennaStatus(false).Sentence);
        }

        [Fact]
        public void ToWireString_AppendsCrLf()
        {
            Assert.Equal("$PMTK605*31\r\n", PmtkCommand.QueryFirmware().ToWireString());
        }

        [Fact]
        public void Type_IsExposedForAckMatching()
        {
            Assert.Equal(220, PmtkCommand.SetNmeaUpdateRate(10).Type);
            Assert.Equal(314, PmtkCommand.SetNmeaOutput(NmeaOutput.RmcGga).Type);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        public void SetNmeaUpdateRate_RejectsOutOfRange(int hz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PmtkCommand.SetNmeaUpdateRate(hz));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(6)]
        [InlineData(10)]
        public void SetFixRate_RejectsAboveChipMaximum(int hz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PmtkCommand.SetFixRate(hz));
        }

        [Fact]
        public void SetBaudRate_RejectsUnsupported()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => PmtkCommand.SetBaudRate(1234));
        }
    }
}
