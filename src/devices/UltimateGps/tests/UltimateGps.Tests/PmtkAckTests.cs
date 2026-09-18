using Iot.Device.UltimateGps;
using Xunit;

namespace UltimateGps.Tests
{
    public class PmtkAckTests
    {
        [Fact]
        public void Parses_SuccessAck()
        {
            Assert.True(PmtkAck.TryParse("$PMTK001,220,3*30", out PmtkAck? ack));
            Assert.NotNull(ack);
            Assert.Equal(220, ack!.CommandType);
            Assert.Equal(PmtkAckFlag.Success, ack.Flag);
            Assert.True(ack.IsSuccess);
        }

        [Fact]
        public void Parses_StandbyAck_FromAdafruitHeader()
        {
            Assert.True(PmtkAck.TryParse("$PMTK001,161,3*36\r\n", out PmtkAck? ack));
            Assert.Equal(161, ack!.CommandType);
            Assert.True(ack.IsSuccess);
        }

        [Theory]
        [InlineData("$PMTK001,314,0*35", PmtkAckFlag.Invalid)]
        [InlineData("$PMTK001,314,1*34", PmtkAckFlag.Unsupported)]
        [InlineData("$PMTK001,314,2*37", PmtkAckFlag.Failed)]
        public void Parses_FailureFlags(string line, PmtkAckFlag expected)
        {
            Assert.True(PmtkAck.TryParse(line, out PmtkAck? ack));
            Assert.Equal(expected, ack!.Flag);
            Assert.False(ack.IsSuccess);
        }

        [Fact]
        public void Rejects_BadChecksum()
        {
            Assert.False(PmtkAck.TryParse("$PMTK001,220,3*31", out _));
        }

        [Fact]
        public void Accepts_MissingChecksum()
        {
            Assert.True(PmtkAck.TryParse("$PMTK001,220,3", out PmtkAck? ack));
            Assert.Equal(220, ack!.CommandType);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47")]
        [InlineData("$PMTK705,AXN_2.31_3339_13101700,5632,PA6H,1.0*6B")]
        [InlineData("$PMTK001,abc,3")]
        [InlineData("$PMTK001,220,9")]
        [InlineData("garbage")]
        public void Rejects_NonAckLines(string? line)
        {
            Assert.False(PmtkAck.TryParse(line, out PmtkAck? ack));
            Assert.Null(ack);
        }
    }
}
