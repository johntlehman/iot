using System.Device.Gpio;
using Iot.Device.Rfm95w;
using UnitsNet;
using Xunit;

namespace Rfm95w.Tests
{
    public class Rfm95wTests
    {
        private const int ResetPin = 25;
        private const int Dio0Pin = 24;

        private static (Iot.Device.Rfm95w.Rfm95w Radio, FakeSx1276 Chip, FakeGpioDriver Gpio) Create(int dio0 = Dio0Pin, double megahertz = 915)
        {
            FakeSx1276 chip = new();
            FakeGpioDriver gpio = new();
            GpioController controller = new(gpio);
            Iot.Device.Rfm95w.Rfm95w radio = new(chip, Frequency.FromMegahertz(megahertz), ResetPin, dio0, controller);
            return (radio, chip, gpio);
        }

        [Fact]
        public void Constructor_PulsesResetLowThenHigh()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                // OpenPin initial High, then reset Low, then High.
                Assert.Equal(new[] { (ResetPin, PinValue.High), (ResetPin, PinValue.Low), (ResetPin, PinValue.High) }, gpio.WriteLog);
            }
        }

        [Fact]
        public void Constructor_Throws_OnWrongVersion()
        {
            FakeSx1276 chip = new();
            chip[Rfm95wRegister.Version] = 0x22; // SX1272
            GpioController controller = new(new FakeGpioDriver());

            Assert.Throws<IOException>(() => new Iot.Device.Rfm95w.Rfm95w(chip, Frequency.FromMegahertz(915), ResetPin, Dio0Pin, controller));
        }

        [Fact]
        public void Constructor_EntersLoRaSleepFirst_ThenStandby()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                Assert.Equal(0x80, chip.OpModeWrites[0]);            // LongRangeMode | Sleep, HF band
                Assert.Equal(0x81, chip.OpModeWrites[^1]);           // LongRangeMode | Standby
                Assert.Equal(OperatingMode.Standby, radio.Mode);
            }
        }

        [Fact]
        public void Constructor_AppliesDefaults()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                Assert.Equal(0x00, chip[Rfm95wRegister.FifoTxBaseAddr]);
                Assert.Equal(0x00, chip[Rfm95wRegister.FifoRxBaseAddr]);
                Assert.Equal(0x23, chip[Rfm95wRegister.Lna]);              // G1 + boost
                Assert.Equal(0x04, chip[Rfm95wRegister.ModemConfig3]);     // AGC auto, no LDRO at SF7
                Assert.Equal(0x72, chip[Rfm95wRegister.ModemConfig1]);     // 125 kHz, 4/5, explicit
                Assert.Equal(0x74, chip[Rfm95wRegister.ModemConfig2]);     // SF7, CRC on
                Assert.Equal(0x12, chip[Rfm95wRegister.SyncWord]);
                Assert.Equal(0x00, chip[Rfm95wRegister.PreambleMsb]);
                Assert.Equal(0x08, chip[Rfm95wRegister.PreambleLsb]);
                Assert.Equal(0x8F, chip[Rfm95wRegister.PaConfig]);         // PA_BOOST, 17 dBm
                Assert.Equal(0x84, chip[Rfm95wRegister.PaDac]);
                Assert.Equal(0x00, chip[Rfm95wRegister.IrqFlagsMask]);
                Assert.Equal(17, radio.TxPower);
                Assert.Equal(SpreadingFactor.Sf7, radio.SpreadingFactor);
                Assert.Equal(LoRaBandwidth.Bw125kHz, radio.Bandwidth);
                Assert.Equal(CodingRate.FourFifths, radio.CodingRate);
                Assert.True(radio.CrcEnabled);
            }
        }

        [Theory]
        [InlineData(915, 0xE4, 0xC0, 0x00)]
        [InlineData(868, 0xD9, 0x00, 0x00)]
        [InlineData(433, 0x6C, 0x40, 0x00)]
        public void Frequency_WritesFrf(double megahertz, byte msb, byte mid, byte lsb)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create(megahertz: megahertz);
            using (radio)
            {
                Assert.Equal(msb, chip[Rfm95wRegister.FrfMsb]);
                Assert.Equal(mid, chip[Rfm95wRegister.FrfMid]);
                Assert.Equal(lsb, chip[Rfm95wRegister.FrfLsb]);
                Assert.Equal(megahertz, radio.Frequency.Megahertz, precision: 6);
            }
        }

        [Fact]
        public void Frequency_BelowMidBand_SetsLowFrequencyModeBit()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create(megahertz: 433);
            using (radio)
            {
                Assert.Equal(0x89, chip[Rfm95wRegister.OpMode]); // LoRa | LF | Standby
            }
        }

        [Fact]
        public void Frequency_RejectsOutOfRange()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.Frequency = Frequency.FromMegahertz(100));
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.Frequency = Frequency.FromMegahertz(2400));
            }
        }

        [Theory]
        [InlineData(SpreadingFactor.Sf7, 0x74)]
        [InlineData(SpreadingFactor.Sf9, 0x94)]
        [InlineData(SpreadingFactor.Sf12, 0xC4)]
        public void SpreadingFactor_WritesModemConfig2(SpreadingFactor sf, byte expected)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.SpreadingFactor = sf;
                Assert.Equal(expected, chip[Rfm95wRegister.ModemConfig2]);
            }
        }

        [Fact]
        public void SpreadingFactor_RejectsSf6()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.SpreadingFactor = (SpreadingFactor)6);
            }
        }

        [Theory]
        [InlineData(LoRaBandwidth.Bw125kHz, CodingRate.FourFifths, 0x72)]
        [InlineData(LoRaBandwidth.Bw250kHz, CodingRate.FourFifths, 0x82)]
        [InlineData(LoRaBandwidth.Bw500kHz, CodingRate.FourEighths, 0x98)]
        [InlineData(LoRaBandwidth.Bw7_8kHz, CodingRate.FourSixths, 0x04)]
        public void BandwidthAndCodingRate_WriteModemConfig1(LoRaBandwidth bw, CodingRate cr, byte expected)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.Bandwidth = bw;
                radio.CodingRate = cr;
                Assert.Equal(expected, chip[Rfm95wRegister.ModemConfig1]);
            }
        }

        [Theory]
        [InlineData(SpreadingFactor.Sf10, LoRaBandwidth.Bw125kHz, false)] // 8.2 ms symbol
        [InlineData(SpreadingFactor.Sf11, LoRaBandwidth.Bw125kHz, true)]  // 16.4 ms
        [InlineData(SpreadingFactor.Sf12, LoRaBandwidth.Bw125kHz, true)]  // 32.8 ms
        [InlineData(SpreadingFactor.Sf12, LoRaBandwidth.Bw500kHz, false)] // 8.2 ms
        [InlineData(SpreadingFactor.Sf7, LoRaBandwidth.Bw7_8kHz, true)]   // 16.4 ms
        public void LowDataRateOptimize_FollowsSymbolTime(SpreadingFactor sf, LoRaBandwidth bw, bool expected)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.Bandwidth = bw;
                radio.SpreadingFactor = sf;
                Assert.Equal(expected, radio.LowDataRateOptimizeEnabled);
                Assert.Equal(0x04, chip[Rfm95wRegister.ModemConfig3] & 0x04); // AGC bit preserved
            }
        }

        [Fact]
        public void CrcEnabled_TogglesModemConfig2Bit2()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.CrcEnabled = false;
                Assert.Equal(0x70, chip[Rfm95wRegister.ModemConfig2]);
                radio.CrcEnabled = true;
                Assert.Equal(0x74, chip[Rfm95wRegister.ModemConfig2]);
            }
        }

        [Theory]
        [InlineData(2, 0x80, 0x84, 0x2B)]
        [InlineData(10, 0x88, 0x84, 0x2B)]
        [InlineData(17, 0x8F, 0x84, 0x2B)]
        [InlineData(20, 0x8F, 0x87, 0x31)]
        public void TxPower_WritesPaConfigPaDacAndOcp(int dbm, byte paConfig, byte paDac, byte ocp)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.TxPower = dbm;
                Assert.Equal(paConfig, chip[Rfm95wRegister.PaConfig]);
                Assert.Equal(paDac, chip[Rfm95wRegister.PaDac]);
                Assert.Equal(ocp, chip[Rfm95wRegister.Ocp]);
                Assert.Equal(dbm, radio.TxPower);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(21)]
        public void TxPower_RejectsOutOfRange(int dbm)
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.TxPower = dbm);
            }
        }

        [Fact]
        public void PreambleAndSyncWord_RoundTrip()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.PreambleLength = 0x0123;
                radio.SyncWord = 0x34;
                Assert.Equal(0x01, chip[Rfm95wRegister.PreambleMsb]);
                Assert.Equal(0x23, chip[Rfm95wRegister.PreambleLsb]);
                Assert.Equal(0x34, chip[Rfm95wRegister.SyncWord]);
                Assert.Equal(0x0123, radio.PreambleLength);
                Assert.Equal(0x34, radio.SyncWord);
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.PreambleLength = 5);
            }
        }

        [Fact]
        public void GetTimeOnAir_MatchesSemtechCalculator()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                // SF7, 125 kHz, CR 4/5, 8 preamble, CRC, explicit header, 10 bytes: 41.216 ms
                Assert.Equal(41.216, radio.GetTimeOnAir(10).TotalMilliseconds, precision: 2);

                // SF12, 125 kHz, CR 4/5, 8 preamble, CRC, explicit, LDRO on, 10 bytes: 991.232 ms
                radio.SpreadingFactor = SpreadingFactor.Sf12;
                Assert.Equal(991.232, radio.GetTimeOnAir(10).TotalMilliseconds, precision: 2);
            }
        }

        [Fact]
        public void Send_WritesPayloadToFifoAndTransmits()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                byte[] payload = { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };
                chip.Writes.Clear();
                chip.OpModeWrites.Clear();

                radio.Send(payload);

                Assert.Equal(payload, chip.Fifo.Take(payload.Length));
                Assert.Equal(payload.Length, chip[Rfm95wRegister.PayloadLength]);
                Assert.Contains((byte)0x83, chip.OpModeWrites);                       // LoRa | TX
                Assert.Equal(0, chip[Rfm95wRegister.IrqFlags] & 0x08);              // TxDone cleared
                Assert.Equal(OperatingMode.Standby, radio.Mode);
                // FIFO pointer reset to 0 before the payload was written.
                int ptrIndex = chip.Writes.FindIndex(w => w.Register == (byte)Rfm95wRegister.FifoAddrPtr && w.Value == 0);
                int lenIndex = chip.Writes.FindIndex(w => w.Register == (byte)Rfm95wRegister.PayloadLength);
                Assert.True(ptrIndex >= 0 && lenIndex > ptrIndex);
            }
        }

        [Fact]
        public void Send_RejectsEmptyAndOversizedPayloads()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.Send(Array.Empty<byte>()));
                Assert.Throws<ArgumentOutOfRangeException>(() => radio.Send(new byte[256]));
            }
        }

        [Fact]
        public void Send_TimesOut_WhenTxDoneNeverComes()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                chip.AutoTxDone = false;

                Assert.Throws<TimeoutException>(() => radio.Send(new byte[] { 1 }, TimeSpan.FromMilliseconds(20)));
                Assert.Equal(OperatingMode.Standby, radio.Mode);
            }
        }

        [Fact]
        public void Send_WhileReceiving_ResumesReceive()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.StartReceive();
                radio.Send(new byte[] { 1, 2, 3 });

                Assert.True(radio.IsReceiving);
                Assert.Equal(OperatingMode.ReceiveContinuous, radio.Mode);
                Assert.Equal(0x85, chip.OpModeWrites[^1]);
            }
        }

        [Fact]
        public void StartReceive_MapsDio0ToRxDoneAndEntersContinuousRx()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                radio.StartReceive();

                Assert.True(radio.IsReceiving);
                Assert.Equal(0x00, chip[Rfm95wRegister.DioMapping1]);
                Assert.Equal(OperatingMode.ReceiveContinuous, radio.Mode);
                Assert.Equal(1, gpio.CallbackCount(Dio0Pin));
            }
        }

        [Fact]
        public void PacketReceived_FiresOnDio0_WithPayloadRssiAndSnr()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                List<LoRaPacket> packets = new();
                radio.PacketReceived += packets.Add;
                radio.StartReceive();

                byte[] payload = { 0x01, 0x02, 0x03, 0x04 };
                chip.InjectPacket(payload, rawSnr: 40, rawRssi: 100); // SNR +10 dB, RSSI -157 + 100*16/15
                gpio.Fire(Dio0Pin, PinEventTypes.Rising);

                LoRaPacket packet = Assert.Single(packets);
                Assert.Equal(payload, packet.Payload);
                Assert.Equal(10.0, packet.Snr, precision: 2);
                Assert.Equal(-157 + 100 * 16.0 / 15.0, packet.Rssi, precision: 2);
                Assert.Equal(0, chip[Rfm95wRegister.IrqFlags] & 0x40); // RxDone cleared
                Assert.Equal(0, radio.CrcErrorCount);
            }
        }

        [Fact]
        public void PacketReceived_NegativeSnr_AddsSnrToRssi()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                List<LoRaPacket> packets = new();
                radio.PacketReceived += packets.Add;
                radio.StartReceive();

                chip.InjectPacket(new byte[] { 9 }, rawSnr: -20, rawRssi: 40); // SNR -5 dB, RSSI -157 + 40 - 5
                gpio.Fire(Dio0Pin, PinEventTypes.Rising);

                LoRaPacket packet = Assert.Single(packets);
                Assert.Equal(-5.0, packet.Snr, precision: 2);
                Assert.Equal(-122.0, packet.Rssi, precision: 2);
            }
        }

        [Fact]
        public void PacketReceived_DropsCrcErrors_AndCounts()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                List<LoRaPacket> packets = new();
                radio.PacketReceived += packets.Add;
                radio.StartReceive();

                chip.InjectPacket(new byte[] { 1, 2 }, crcError: true);
                gpio.Fire(Dio0Pin, PinEventTypes.Rising);

                Assert.Empty(packets);
                Assert.Equal(1, radio.CrcErrorCount);
                Assert.Equal(0, chip[Rfm95wRegister.IrqFlags] & 0x60); // both flags cleared
            }
        }

        [Fact]
        public void PacketReceived_ReadsFromFifoRxCurrentAddr()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                List<LoRaPacket> packets = new();
                radio.PacketReceived += packets.Add;
                radio.StartReceive();

                // Second packet lands further into the FIFO, as the chip does in continuous mode.
                chip[Rfm95wRegister.FifoRxBaseAddr] = 0x40;
                chip.InjectPacket(new byte[] { 0xAA, 0xBB });
                gpio.Fire(Dio0Pin, PinEventTypes.Rising);

                Assert.Equal(new byte[] { 0xAA, 0xBB }, Assert.Single(packets).Payload);
            }
        }

        [Fact]
        public void Receive_PollingWithoutDio0_ReturnsPacket()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create(dio0: -1);
            using (radio)
            {
                Assert.DoesNotContain(Dio0Pin, gpio.OpenPins);
                radio.StartReceive();                       // a real chip only receives once in RX mode
                chip.InjectPacket(new byte[] { 7, 8, 9 });

                LoRaPacket? packet = radio.Receive(TimeSpan.FromSeconds(1));

                Assert.NotNull(packet);
                Assert.Equal(new byte[] { 7, 8, 9 }, packet!.Payload);
                Assert.True(radio.IsReceiving);
            }
        }

        [Fact]
        public void Receive_ReturnsNull_OnTimeout()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create(dio0: -1);
            using (radio)
            {
                Assert.Null(radio.Receive(TimeSpan.FromMilliseconds(20)));
            }
        }

        [Fact]
        public void StopReceive_UnregistersCallbackAndGoesToStandby()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                radio.StartReceive();
                radio.StopReceive();

                Assert.False(radio.IsReceiving);
                Assert.Equal(0, gpio.CallbackCount(Dio0Pin));
                Assert.Equal(OperatingMode.Standby, radio.Mode);
            }
        }

        [Fact]
        public void Dio0_WhileNotReceiving_IsIgnored()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            using (radio)
            {
                List<LoRaPacket> packets = new();
                radio.PacketReceived += packets.Add;
                radio.StartReceive();
                radio.StopReceive();

                chip.InjectPacket(new byte[] { 1 });
                gpio.Fire(Dio0Pin, PinEventTypes.Rising);

                Assert.Empty(packets);
            }
        }

        [Fact]
        public void SleepAndStandby_WriteOpMode()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, _, _) = Create();
            using (radio)
            {
                radio.Sleep();
                Assert.Equal(OperatingMode.Sleep, radio.Mode);
                radio.Standby();
                Assert.Equal(OperatingMode.Standby, radio.Mode);
            }
        }

        [Fact]
        public void Dispose_SleepsRadio_ClosesPins_DisposesSpi()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, FakeGpioDriver gpio) = Create();
            radio.StartReceive();

            radio.Dispose();

            Assert.Equal(0x80, chip[Rfm95wRegister.OpMode]);
            Assert.True(chip.Disposed);
            Assert.Empty(gpio.OpenPins);
            Assert.Throws<ObjectDisposedException>(() => radio.Send(new byte[] { 1 }));
        }

        [Fact]
        public void ReadWriteRegister_UseCorrectSpiFraming()
        {
            (Iot.Device.Rfm95w.Rfm95w radio, FakeSx1276 chip, _) = Create();
            using (radio)
            {
                radio.WriteRegister(Rfm95wRegister.HopPeriod, 0x5A);
                Assert.Equal(0x5A, chip[Rfm95wRegister.HopPeriod]);
                Assert.Equal(0x5A, radio.ReadRegister(Rfm95wRegister.HopPeriod));
                Assert.Contains(((byte)Rfm95wRegister.HopPeriod, (byte)0x5A), chip.Writes);
            }
        }
    }
}
