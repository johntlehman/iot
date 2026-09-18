using Iot.Device.Nmea0183.Sentences;
using Iot.Device.UltimateGps;
using Xunit;

namespace UltimateGps.Tests
{
    public class UltimateGpsTests
    {
        // Real MTK3339 output shape; checksums computed for these exact strings.
        private const string Gga1 = "$GPGGA,123519.000,4807.0380,N,01131.0000,E,1,08,0.9,545.4,M,46.9,M,,*59";
        private const string Rmc1 = "$GPRMC,123519.000,A,4807.0380,N,01131.0000,E,022.4,084.4,230394,003.1,W*74";
        private const string Gga2 = "$GPGGA,123519.100,4807.0381,N,01131.0001,E,1,08,0.9,546.0,M,46.9,M,,*5F";
        private const string Rmc2 = "$GPRMC,123519.100,A,4807.0381,N,01131.0001,E,022.6,084.5,230394,003.1,W*76";
        private const string GgaNoFix = "$GPGGA,000000.000,,,,,0,00,,,M,,M,,*";

        private static string WithChecksum(string sentenceWithoutChecksum)
        {
            string body = sentenceWithoutChecksum.TrimStart('$').TrimEnd('*');
            return $"${body}*{PmtkCommand.Checksum(body):X2}";
        }

        private static (Iot.Device.UltimateGps.UltimateGps Gps, FakeSerialStream Stream) CreateStarted(Func<int, bool>? baudChanger = null)
        {
            FakeSerialStream stream = new();
            Iot.Device.UltimateGps.UltimateGps gps = new(stream, baudChanger);
            gps.Start();
            return (gps, stream);
        }

        [Fact]
        public void Checksums_OfTestSentences_AreValid()
        {
            // Guard against typos in the constants above: the parser rejects bad checksums silently.
            Assert.Equal(Gga1, WithChecksum(Gga1.Substring(0, Gga1.IndexOf('*') + 1)));
            Assert.Equal(Rmc1, WithChecksum(Rmc1.Substring(0, Rmc1.IndexOf('*') + 1)));
            Assert.Equal(Gga2, WithChecksum(Gga2.Substring(0, Gga2.IndexOf('*') + 1)));
            Assert.Equal(Rmc2, WithChecksum(Rmc2.Substring(0, Rmc2.IndexOf('*') + 1)));
        }

        [Fact]
        public void ProcessLine_GgaAndRmcSameEpoch_EmitsOneMergedFix()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<GpsFix> fixes = new();
            gps.FixUpdated += fixes.Add;

            gps.ProcessLine(Gga1);
            gps.ProcessLine(Rmc1);

            GpsFix fix = Assert.Single(fixes);
            Assert.True(fix.HasFix);
            Assert.Equal(GpsQuality.Fix, fix.Quality);
            Assert.Equal(48.1173, fix.Latitude, precision: 4);
            Assert.Equal(11.5167, fix.Longitude, precision: 4);
            Assert.Equal(545.4, fix.Altitude!.Value.Meters, precision: 1);
            Assert.Equal(8, fix.SatellitesInUse);
            Assert.Equal(0.9, fix.Hdop, precision: 1);
            Assert.Equal(22.4, fix.SpeedOverGround!.Value.Knots, precision: 1);
            Assert.Equal(84.4, fix.Course!.Value.Degrees, precision: 1);
            Assert.Equal(new DateTime(1994, 3, 23, 12, 35, 19), fix.Time.UtcDateTime);
            Assert.Same(fix, gps.LastFix);
        }

        [Fact]
        public void ProcessLine_TwoEpochs_EmitTwoFixes()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<GpsFix> fixes = new();
            gps.FixUpdated += fixes.Add;

            gps.ProcessLine(Gga1);
            gps.ProcessLine(Rmc1);
            gps.ProcessLine(Gga2);
            gps.ProcessLine(Rmc2);

            Assert.Equal(2, fixes.Count);
            Assert.Equal(545.4, fixes[0].Altitude!.Value.Meters, precision: 1);
            Assert.Equal(546.0, fixes[1].Altitude!.Value.Meters, precision: 1);
            Assert.Equal(100, (fixes[1].Time - fixes[0].Time).TotalMilliseconds, precision: 0);
        }

        [Fact]
        public void ProcessLine_GgaOnly_EmitsAfterRmcIsDeemedAbsent()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<GpsFix> fixes = new();
            gps.FixUpdated += fixes.Add;

            gps.ProcessLine(Gga1);
            gps.ProcessLine(Gga2);
            gps.ProcessLine(Gga1);
            gps.ProcessLine(Gga2);

            Assert.NotEmpty(fixes);
            Assert.All(fixes, f => Assert.Null(f.SpeedOverGround));
            Assert.All(fixes, f => Assert.True(f.HasFix));
        }

        [Fact]
        public void ProcessLine_NoFixGga_ReportsHasFixFalse()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<GpsFix> fixes = new();
            gps.FixUpdated += fixes.Add;
            string noFix = WithChecksum(GgaNoFix);

            for (int i = 0; i < 5; i++)
            {
                gps.ProcessLine(noFix);
            }

            Assert.NotEmpty(fixes);
            Assert.All(fixes, f => Assert.False(f.HasFix));
            Assert.Equal(GpsQuality.NoFix, fixes[^1].Quality);
            Assert.Equal(0, fixes[^1].SatellitesInUse);
        }

        [Fact]
        public void ProcessLine_BadChecksum_RaisesParserErrorAndNoFix()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<GpsFix> fixes = new();
            List<(string, Iot.Device.Nmea0183.NmeaError)> errors = new();
            gps.FixUpdated += fixes.Add;
            gps.ParserError += (l, e) => errors.Add((l, e));

            gps.ProcessLine("$GPGGA,123519.000,4807.0380,N,01131.0000,E,1,08,0.9,545.4,M,46.9,M,,*00");

            Assert.Empty(fixes);
            (_, Iot.Device.Nmea0183.NmeaError error) = Assert.Single(errors);
            Assert.Equal(Iot.Device.Nmea0183.NmeaError.InvalidChecksum, error);
        }

        [Fact]
        public void SentenceReceived_FiresForEveryLine()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());
            List<string> lines = new();
            gps.SentenceReceived += lines.Add;

            gps.ProcessLine(Gga1);
            gps.ProcessLine("$PMTK001,220,3*30");

            Assert.Equal(2, lines.Count);
        }

        [Fact]
        public async Task ReaderThread_ParsesLinesFromStream()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            using (gps)
            {
                TaskCompletionSource<GpsFix> got = new(TaskCreationOptions.RunContinuationsAsynchronously);
                gps.FixUpdated += f => got.TrySetResult(f);

                // Split across two chunks and use CR LF like the real module.
                stream.Receive(Gga1 + "\r\n" + Rmc1.Substring(0, 20));
                stream.Receive(Rmc1.Substring(20) + "\r\n");

                GpsFix fix = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(fix.HasFix);
                Assert.Equal(545.4, fix.Altitude!.Value.Meters, precision: 1);
            }
        }

        [Fact]
        public async Task SendCommandAsync_ReturnsAck_WhenModuleAnswers()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            using (gps)
            {
                stream.Sent += line =>
                {
                    if (line == "$PMTK220,100*2F")
                    {
                        stream.Receive("$PMTK001,220,3*30\r\n");
                    }
                };

                PmtkAck ack = await gps.SendCommandAsync(PmtkCommand.SetNmeaUpdateRate(10), TimeSpan.FromSeconds(5));

                Assert.True(ack.IsSuccess);
                Assert.Equal(220, ack.CommandType);
                Assert.Equal("$PMTK220,100*2F", Assert.Single(stream.Written));
            }
        }

        [Fact]
        public async Task SendCommandAsync_IgnoresAckForOtherCommand()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            using (gps)
            {
                stream.Sent += _ =>
                {
                    stream.Receive("$PMTK001,314,3*36\r\n"); // wrong type
                    stream.Receive("$PMTK001,220,3*30\r\n"); // right type
                };

                PmtkAck ack = await gps.SendCommandAsync(PmtkCommand.SetNmeaUpdateRate(10), TimeSpan.FromSeconds(5));

                Assert.Equal(220, ack.CommandType);
            }
        }

        [Fact]
        public async Task SendCommandAsync_TimesOut_WhenNoAck()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, _) = CreateStarted();
            using (gps)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => gps.SendCommandAsync(PmtkCommand.SetNmeaUpdateRate(10), TimeSpan.FromMilliseconds(100)));
            }
        }

        [Fact]
        public async Task SendCommandAsync_Throws_WhenNotStarted()
        {
            using Iot.Device.UltimateGps.UltimateGps gps = new(new FakeSerialStream());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => gps.SendCommandAsync(PmtkCommand.SetNmeaUpdateRate(10), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task QueryFirmwareAsync_ReturnsPmtk705Payload()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            using (gps)
            {
                stream.Sent += line =>
                {
                    if (line == "$PMTK605*31")
                    {
                        stream.Receive("$PMTK705,AXN_2.31_3339_13101700,5632,PA6H,1.0*6B\r\n");
                    }
                };

                string release = await gps.QueryFirmwareAsync(TimeSpan.FromSeconds(5));

                Assert.Equal("AXN_2.31_3339_13101700,5632,PA6H,1.0", release);
                Assert.Equal(release, gps.FirmwareRelease);
            }
        }

        [Fact]
        public async Task ConfigureForFlightAsync_SendsCommandsInOrder_AndSwitchesBaud()
        {
            List<int> baudChanges = new();
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted(b =>
            {
                baudChanges.Add(b);
                return true;
            });
            using (gps)
            {
                stream.Sent += line =>
                {
                    // Module acknowledges everything except the baud change.
                    if (line.StartsWith("$PMTK314")) stream.Receive("$PMTK001,314,3*36\r\n");
                    if (line.StartsWith("$PMTK300")) stream.Receive("$PMTK001,300,3*33\r\n");
                    if (line.StartsWith("$PMTK220")) stream.Receive("$PMTK001,220,3*30\r\n");
                };

                await gps.ConfigureForFlightAsync(ackTimeout: TimeSpan.FromSeconds(5));

                Assert.Equal(
                    new[]
                    {
                        "$PMTK251,57600*2C",
                        "$PMTK314,0,1,0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0*28",
                        "$PMTK300,200,0,0,0,0*2F",
                        "$PMTK220,100*2F",
                    },
                    stream.Written);
                Assert.Equal(new[] { 57600 }, baudChanges);
            }
        }

        [Fact]
        public async Task ConfigureForFlightAsync_Throws_WhenModuleRejects()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            using (gps)
            {
                stream.Sent += line =>
                {
                    if (line.StartsWith("$PMTK314")) stream.Receive("$PMTK001,314,2*37\r\n"); // Failed
                };

                await Assert.ThrowsAsync<IOException>(
                    () => gps.ConfigureForFlightAsync(baud: 0, ackTimeout: TimeSpan.FromSeconds(5)));
            }
        }

        [Fact]
        public async Task ConfigureForFlightAsync_TimesOut_WhenNoAck()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, _) = CreateStarted();
            using (gps)
            {
                await Assert.ThrowsAsync<TimeoutException>(
                    () => gps.ConfigureForFlightAsync(baud: 0, ackTimeout: TimeSpan.FromMilliseconds(100)));
            }
        }

        [Fact]
        public async Task SetBaudRateAsync_Throws_WithoutBaudChanger()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, _) = CreateStarted();
            using (gps)
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => gps.SetBaudRateAsync(57600));
            }
        }

        [Fact]
        public void Dispose_StopsReaderAndDisposesStream()
        {
            (Iot.Device.UltimateGps.UltimateGps gps, FakeSerialStream stream) = CreateStarted();
            Assert.True(gps.IsRunning);

            gps.Dispose();

            Assert.False(gps.IsRunning);
            Assert.True(stream.Disposed);
        }

        [Fact]
        public void Dispose_LeavesStream_WhenShouldDisposeIsFalse()
        {
            FakeSerialStream stream = new();
            Iot.Device.UltimateGps.UltimateGps gps = new(stream, shouldDispose: false);

            gps.Dispose();

            Assert.False(stream.Disposed);
        }
    }
}
