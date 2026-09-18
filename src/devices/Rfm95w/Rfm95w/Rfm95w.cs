using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;
using UnitsNet;

namespace Iot.Device.Rfm95w
{
    /// <summary>
    /// Driver for the HopeRF RFM95W / RFM96W / RFM98W LoRa module (Semtech SX1276/77/78) in LoRa mode, as used on
    /// the Adafruit RFM95W breakout (product 3072). Point-to-point packets only, no LoRaWAN.
    /// </summary>
    /// <remarks>
    /// SPI: mode 0, MSB first, up to 10 MHz. A register read sends the address with bit 7 clear, a write with bit 7
    /// set. The reset pin is active low. DIO0 is optional: with it, receive is interrupt driven; without it, use
    /// <see cref="Receive"/> to poll.
    /// </remarks>
    public sealed class Rfm95w : IDisposable
    {
        /// <summary>RegVersion value of every SX1276-family die.</summary>
        public const byte ExpectedVersion = 0x12;

        /// <summary>Largest LoRa payload.</summary>
        public const int MaxPayloadLength = 255;

        /// <summary>A safe SPI clock for jumper wires; the chip allows 10 MHz.</summary>
        public const int DefaultSpiClockFrequency = 4_000_000;

        /// <summary>Private (non-LoRaWAN) sync word, the chip default.</summary>
        public const byte DefaultSyncWord = 0x12;

        private const double CrystalFrequency = 32_000_000.0;
        private const double FrequencyStep = CrystalFrequency / (1 << 19); // 61.035 Hz
        private const double MidBandThreshold = 525_000_000.0;             // below: LF port, above: HF port
        private const int RssiOffsetLf = -164;
        private const int RssiOffsetHf = -157;

        private readonly SpiDevice _spi;
        private readonly GpioController _gpio;
        private readonly bool _shouldDisposeGpio;
        private readonly int _resetPin;
        private readonly int _dio0Pin;
        private readonly object _lock = new();

        private Frequency _frequency;
        private SpreadingFactor _spreadingFactor = SpreadingFactor.Sf7;
        private LoRaBandwidth _bandwidth = LoRaBandwidth.Bw125kHz;
        private CodingRate _codingRate = CodingRate.FourFifths;
        private ushort _preambleLength = 8;
        private int _txPower = 17;
        private bool _crcEnabled = true;
        private bool _receiving;
        private bool _disposed;

        /// <summary>
        /// Creates the driver, resets the radio and puts it in LoRa standby with the given frequency and defaults
        /// (SF7, 125 kHz, CR 4/5, 17 dBm, CRC on, sync word 0x12).
        /// </summary>
        /// <param name="spi">SPI device on the module's chip select.</param>
        /// <param name="frequency">Carrier frequency, e.g. <c>Frequency.FromMegahertz(915)</c>.</param>
        /// <param name="resetPin">GPIO connected to RST.</param>
        /// <param name="dio0Pin">GPIO connected to G0/DIO0, or -1 for polling-only receive.</param>
        /// <param name="gpioController">Controller for the pins; a default one is created when null.</param>
        /// <param name="shouldDispose">True to dispose the GPIO controller with this object (always true when it was created here).</param>
        public Rfm95w(SpiDevice spi, Frequency frequency, int resetPin, int dio0Pin = -1, GpioController? gpioController = null, bool shouldDispose = true)
        {
            _spi = spi ?? throw new ArgumentNullException(nameof(spi));
            _gpio = gpioController ?? new GpioController();
            _shouldDisposeGpio = shouldDispose || gpioController is null;
            _resetPin = resetPin;
            _dio0Pin = dio0Pin;

            _gpio.OpenPin(_resetPin, PinMode.Output, PinValue.High);
            if (_dio0Pin >= 0)
            {
                _gpio.OpenPin(_dio0Pin, PinMode.Input);
            }

            Reset();

            byte version = ReadRegister(Rfm95wRegister.Version);
            if (version != ExpectedVersion)
            {
                throw new IOException($"Expected SX1276 version 0x{ExpectedVersion:X2} but read 0x{version:X2}. Check wiring, chip select and reset.");
            }

            // The LoRa bit can only change in sleep.
            SetMode(OperatingMode.Sleep);
            Frequency = frequency;
            WriteRegister(Rfm95wRegister.FifoTxBaseAddr, 0x00);
            WriteRegister(Rfm95wRegister.FifoRxBaseAddr, 0x00);
            WriteRegister(Rfm95wRegister.Lna, (byte)(ReadRegister(Rfm95wRegister.Lna) | 0x03)); // LNA boost on HF port
            WriteRegister(Rfm95wRegister.ModemConfig3, 0x04);                                     // AGC auto on
            WriteRegister(Rfm95wRegister.IrqFlagsMask, 0x00);                                     // all IRQs unmasked
            ApplyModemConfig();
            PreambleLength = _preambleLength;
            SyncWord = DefaultSyncWord;
            TxPower = _txPower;
            SetMode(OperatingMode.Standby);
        }

        /// <summary>
        /// SPI settings for the module: mode 0, 8 bits, 4 MHz.
        /// </summary>
        public static SpiConnectionSettings GetDefaultSpiSettings(int busId = 0, int chipSelectLine = 0) =>
            new(busId, chipSelectLine)
            {
                Mode = SpiMode.Mode0,
                DataBitLength = 8,
                ClockFrequency = DefaultSpiClockFrequency,
            };

        /// <summary>Raised on the GPIO event thread for every packet that passes the CRC check while receiving.</summary>
        public event Action<LoRaPacket>? PacketReceived;

        /// <summary>Number of packets dropped because of a payload CRC error.</summary>
        public int CrcErrorCount { get; private set; }

        /// <summary>True while in continuous receive mode.</summary>
        public bool IsReceiving => _receiving;

        /// <summary>RegVersion, 0x12 for all SX1276-family chips.</summary>
        public byte Version => ReadRegister(Rfm95wRegister.Version);

        /// <summary>Current operating mode from RegOpMode.</summary>
        public OperatingMode Mode => (OperatingMode)(ReadRegister(Rfm95wRegister.OpMode) & (byte)OpModeBits.ModeMask);

        /// <summary>
        /// Carrier frequency. Written to RegFrf as frequency / 61.035 Hz. Setting it also selects the LF/HF port.
        /// </summary>
        public Frequency Frequency
        {
            get => _frequency;
            set
            {
                double hertz = value.Hertz;
                if (hertz < 137e6 || hertz > 1020e6)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "SX1276 covers 137..1020 MHz");
                }

                uint frf = (uint)Math.Round(hertz / FrequencyStep);
                lock (_lock)
                {
                    _frequency = value;
                    WriteRegister(Rfm95wRegister.FrfMsb, (byte)(frf >> 16));
                    WriteRegister(Rfm95wRegister.FrfMid, (byte)(frf >> 8));
                    WriteRegister(Rfm95wRegister.FrfLsb, (byte)frf);
                    // Re-write op mode so the LowFrequencyModeOn bit matches the band.
                    SetMode(Mode);
                }
            }
        }

        /// <summary>Spreading factor, SF7..SF12.</summary>
        public SpreadingFactor SpreadingFactor
        {
            get => _spreadingFactor;
            set
            {
                if (!Enum.IsDefined(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Supported spreading factors are SF7..SF12");
                }

                lock (_lock)
                {
                    _spreadingFactor = value;
                    ApplyModemConfig();
                }
            }
        }

        /// <summary>Signal bandwidth.</summary>
        public LoRaBandwidth Bandwidth
        {
            get => _bandwidth;
            set
            {
                if (!Enum.IsDefined(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                lock (_lock)
                {
                    _bandwidth = value;
                    ApplyModemConfig();
                }
            }
        }

        /// <summary>Coding rate.</summary>
        public CodingRate CodingRate
        {
            get => _codingRate;
            set
            {
                if (!Enum.IsDefined(value))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                lock (_lock)
                {
                    _codingRate = value;
                    ApplyModemConfig();
                }
            }
        }

        /// <summary>Payload CRC on (recommended).</summary>
        public bool CrcEnabled
        {
            get => _crcEnabled;
            set
            {
                lock (_lock)
                {
                    _crcEnabled = value;
                    ApplyModemConfig();
                }
            }
        }

        /// <summary>Preamble length in symbols, 6..65535. Both ends must agree. Default 8.</summary>
        public ushort PreambleLength
        {
            get => _preambleLength;
            set
            {
                if (value < 6)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Preamble must be at least 6 symbols");
                }

                lock (_lock)
                {
                    _preambleLength = value;
                    WriteRegister(Rfm95wRegister.PreambleMsb, (byte)(value >> 8));
                    WriteRegister(Rfm95wRegister.PreambleLsb, (byte)value);
                }
            }
        }

        /// <summary>Sync word. 0x12 is the private default, 0x34 is reserved for LoRaWAN. Both ends must agree.</summary>
        public byte SyncWord
        {
            get => ReadRegister(Rfm95wRegister.SyncWord);
            set => WriteRegister(Rfm95wRegister.SyncWord, value);
        }

        /// <summary>
        /// Output power in dBm on the PA_BOOST pin (the only PA wired on RFM95W), 2..20. 18..20 dBm enables the
        /// high-power DAC and a 140 mA over-current limit; keep the duty cycle low there.
        /// </summary>
        public int TxPower
        {
            get => _txPower;
            set
            {
                if (value < 2 || value > 20)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "TX power must be 2..20 dBm");
                }

                lock (_lock)
                {
                    _txPower = value;
                    int level = value;
                    if (level > 17)
                    {
                        // +20 dBm: PaDac on, PaConfig as for 17 dBm (datasheet 5.4.3), OCP 140 mA.
                        WriteRegister(Rfm95wRegister.PaDac, 0x87);
                        SetOverCurrentProtection(140);
                        level = 17;
                    }
                    else
                    {
                        WriteRegister(Rfm95wRegister.PaDac, 0x84);
                        SetOverCurrentProtection(100);
                    }

                    // PaSelect = PA_BOOST, MaxPower = 0 (unused with PA_BOOST), OutputPower = Pout - 2.
                    WriteRegister(Rfm95wRegister.PaConfig, (byte)(0x80 | (level - 2)));
                }
            }
        }

        /// <summary>True when the low-data-rate-optimize bit is set (symbol time above 16 ms).</summary>
        public bool LowDataRateOptimizeEnabled => (ReadRegister(Rfm95wRegister.ModemConfig3) & 0x08) != 0;

        /// <summary>Current (not packet) RSSI in dBm, useful for a channel-noise reading while receiving.</summary>
        public double Rssi => RssiOffset + ReadRegister(Rfm95wRegister.RssiValue);

        /// <summary>
        /// Time on air for a payload of the given length with the current settings (Semtech AN1200.13, section 4).
        /// </summary>
        public TimeSpan GetTimeOnAir(int payloadLength)
        {
            if (payloadLength < 0 || payloadLength > MaxPayloadLength)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }

            int sf = (int)_spreadingFactor;
            double bw = _bandwidth.ToHertz();
            double symbolTime = Math.Pow(2, sf) / bw;
            double preambleTime = (_preambleLength + 4.25) * symbolTime;
            int de = IsLowDataRateOptimizeNeeded() ? 1 : 0;
            int crc = _crcEnabled ? 1 : 0;
            const int implicitHeader = 0;
            double numerator = 8 * payloadLength - 4 * sf + 28 + 16 * crc - 20 * implicitHeader;
            double denominator = 4 * (sf - 2 * de);
            double payloadSymbols = 8 + Math.Max(Math.Ceiling(numerator / denominator) * ((int)_codingRate + 4), 0);
            return TimeSpan.FromSeconds(preambleTime + payloadSymbols * symbolTime);
        }

        /// <summary>
        /// Transmits one packet and blocks until it is on the air. Leaves the radio in standby, or back in continuous
        /// receive if it was receiving.
        /// </summary>
        /// <param name="payload">1..255 bytes.</param>
        /// <param name="timeout">How long to wait for TxDone; default is twice the time on air plus 100 ms.</param>
        /// <exception cref="TimeoutException">TxDone never came.</exception>
        public void Send(ReadOnlySpan<byte> payload, TimeSpan? timeout = null)
        {
            if (payload.Length == 0 || payload.Length > MaxPayloadLength)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"Payload must be 1..{MaxPayloadLength} bytes");
            }

            TimeSpan wait = timeout ?? GetTimeOnAir(payload.Length) * 2 + TimeSpan.FromMilliseconds(100);
            bool resumeReceive;

            lock (_lock)
            {
                ThrowIfDisposed();
                resumeReceive = _receiving;
                _receiving = false;
                SetMode(OperatingMode.Standby);
                WriteRegister(Rfm95wRegister.IrqFlags, (byte)IrqFlags.All);
                WriteRegister(Rfm95wRegister.FifoAddrPtr, 0x00);
                WriteFifo(payload);
                WriteRegister(Rfm95wRegister.PayloadLength, (byte)payload.Length);
                SetMode(OperatingMode.Transmit);
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                IrqFlags flags;
                lock (_lock)
                {
                    flags = (IrqFlags)ReadRegister(Rfm95wRegister.IrqFlags);
                }

                if (flags.HasFlag(IrqFlags.TxDone))
                {
                    break;
                }

                if (sw.Elapsed > wait)
                {
                    lock (_lock)
                    {
                        SetMode(OperatingMode.Standby);
                    }

                    throw new TimeoutException($"TxDone not raised within {wait.TotalMilliseconds:F0} ms");
                }

                Thread.Sleep(1);
            }

            lock (_lock)
            {
                WriteRegister(Rfm95wRegister.IrqFlags, (byte)IrqFlags.TxDone);
                if (resumeReceive)
                {
                    StartReceiveCore();
                }
            }
        }

        /// <summary>
        /// Enters continuous receive. With a DIO0 pin, <see cref="PacketReceived"/> fires for each packet; without
        /// one, call <see cref="Receive"/> to poll.
        /// </summary>
        public void StartReceive()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (_receiving)
                {
                    return;
                }

                if (_dio0Pin >= 0)
                {
                    _gpio.RegisterCallbackForPinValueChangedEvent(_dio0Pin, PinEventTypes.Rising, OnDio0);
                }

                StartReceiveCore();
            }
        }

        /// <summary>
        /// Leaves receive mode and goes to standby.
        /// </summary>
        public void StopReceive()
        {
            lock (_lock)
            {
                if (!_receiving)
                {
                    return;
                }

                _receiving = false;
                if (_dio0Pin >= 0)
                {
                    _gpio.UnregisterCallbackForPinValueChangedEvent(_dio0Pin, OnDio0);
                }

                SetMode(OperatingMode.Standby);
            }
        }

        /// <summary>
        /// Polls for one packet. Starts continuous receive if not already receiving. Returns null on timeout.
        /// Not needed when a DIO0 pin is wired and <see cref="PacketReceived"/> is used.
        /// </summary>
        public LoRaPacket? Receive(TimeSpan timeout)
        {
            lock (_lock)
            {
                if (!_receiving)
                {
                    StartReceiveCore();
                }
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed <= timeout)
            {
                LoRaPacket? packet;
                lock (_lock)
                {
                    packet = TryReadPacket();
                }

                if (packet != null)
                {
                    return packet;
                }

                Thread.Sleep(1);
            }

            return null;
        }

        /// <summary>Puts the radio to sleep (lowest power). Any operation wakes it.</summary>
        public void Sleep()
        {
            lock (_lock)
            {
                _receiving = false;
                SetMode(OperatingMode.Sleep);
            }
        }

        /// <summary>Puts the radio in standby.</summary>
        public void Standby()
        {
            lock (_lock)
            {
                _receiving = false;
                SetMode(OperatingMode.Standby);
            }
        }

        /// <summary>
        /// Pulses RST (active low) and waits for the chip to boot.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _gpio.Write(_resetPin, PinValue.Low);
                Thread.Sleep(1);   // datasheet 7.2.2: at least 100 us
                _gpio.Write(_resetPin, PinValue.High);
                Thread.Sleep(10);  // at least 5 ms
                _receiving = false;
            }
        }

        private void StartReceiveCore()
        {
            WriteRegister(Rfm95wRegister.DioMapping1, 0x00); // DIO0 = RxDone
            WriteRegister(Rfm95wRegister.IrqFlags, (byte)IrqFlags.All);
            WriteRegister(Rfm95wRegister.FifoAddrPtr, 0x00);
            SetMode(OperatingMode.ReceiveContinuous);
            _receiving = true;
        }

        private void OnDio0(object sender, PinValueChangedEventArgs args)
        {
            LoRaPacket? packet;
            lock (_lock)
            {
                if (_disposed || !_receiving)
                {
                    return;
                }

                packet = TryReadPacket();
            }

            if (packet != null)
            {
                PacketReceived?.Invoke(packet);
            }
        }

        /// <summary>
        /// Reads a pending packet out of the FIFO if RxDone is set. Must be called under the lock.
        /// Clears the RX flags but leaves TxDone alone so a concurrent <see cref="Send"/> still sees it.
        /// </summary>
        private LoRaPacket? TryReadPacket()
        {
            IrqFlags flags = (IrqFlags)ReadRegister(Rfm95wRegister.IrqFlags);
            if (!flags.HasFlag(IrqFlags.RxDone))
            {
                return null;
            }

            WriteRegister(Rfm95wRegister.IrqFlags, (byte)(flags & ~IrqFlags.TxDone));

            if (flags.HasFlag(IrqFlags.PayloadCrcError))
            {
                CrcErrorCount++;
                return null;
            }

            int length = ReadRegister(Rfm95wRegister.RxNbBytes);
            byte currentAddr = ReadRegister(Rfm95wRegister.FifoRxCurrentAddr);
            WriteRegister(Rfm95wRegister.FifoAddrPtr, currentAddr);
            byte[] payload = new byte[length];
            ReadFifo(payload);

            sbyte rawSnr = (sbyte)ReadRegister(Rfm95wRegister.PktSnrValue);
            double snr = rawSnr / 4.0;
            int rawRssi = ReadRegister(Rfm95wRegister.PktRssiValue);
            // Datasheet 5.5.5: below the noise floor add the SNR; above it, correct the slope.
            double rssi = snr < 0 ? RssiOffset + rawRssi + snr : RssiOffset + rawRssi * 16.0 / 15.0;

            return new LoRaPacket(payload, rssi, snr, Stopwatch.GetTimestamp());
        }

        private int RssiOffset => _frequency.Hertz < MidBandThreshold ? RssiOffsetLf : RssiOffsetHf;

        private bool IsLowDataRateOptimizeNeeded()
        {
            double symbolTimeMs = 1000.0 * Math.Pow(2, (int)_spreadingFactor) / _bandwidth.ToHertz();
            return symbolTimeMs > 16.0;
        }

        private void ApplyModemConfig()
        {
            // ModemConfig1: Bw[7:4] CodingRate[3:1] ImplicitHeaderModeOn[0] (explicit header always)
            WriteRegister(Rfm95wRegister.ModemConfig1, (byte)(((byte)_bandwidth << 4) | ((byte)_codingRate << 1)));

            // ModemConfig2: SpreadingFactor[7:4] TxContinuousMode[3] RxPayloadCrcOn[2] SymbTimeout[1:0]
            byte config2 = (byte)(ReadRegister(Rfm95wRegister.ModemConfig2) & 0x03);
            config2 |= (byte)((byte)_spreadingFactor << 4);
            if (_crcEnabled)
            {
                config2 |= 0x04;
            }

            WriteRegister(Rfm95wRegister.ModemConfig2, config2);

            // ModemConfig3: LowDataRateOptimize[3] AgcAutoOn[2]
            byte config3 = (byte)(ReadRegister(Rfm95wRegister.ModemConfig3) & ~0x08);
            if (IsLowDataRateOptimizeNeeded())
            {
                config3 |= 0x08;
            }

            WriteRegister(Rfm95wRegister.ModemConfig3, config3);

            // Detection settings for SF7..SF12 (SF6 would need 0x05 / 0x0C).
            WriteRegister(Rfm95wRegister.DetectOptimize, 0xC3);
            WriteRegister(Rfm95wRegister.DetectionThreshold, 0x0A);
        }

        private void SetOverCurrentProtection(int milliamps)
        {
            int trim;
            if (milliamps <= 120)
            {
                trim = (milliamps - 45) / 5;
            }
            else if (milliamps <= 240)
            {
                trim = (milliamps + 30) / 10;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(milliamps));
            }

            WriteRegister(Rfm95wRegister.Ocp, (byte)(0x20 | (trim & 0x1F)));
        }

        private void SetMode(OperatingMode mode)
        {
            byte value = (byte)((byte)OpModeBits.LongRangeMode | (byte)mode);
            if (_frequency.Hertz > 0 && _frequency.Hertz < MidBandThreshold)
            {
                value |= (byte)OpModeBits.LowFrequencyModeOn;
            }

            WriteRegister(Rfm95wRegister.OpMode, value);
        }

        internal byte ReadRegister(Rfm95wRegister register)
        {
            Span<byte> write = stackalloc byte[2] { (byte)((byte)register & 0x7F), 0x00 };
            Span<byte> read = stackalloc byte[2];
            _spi.TransferFullDuplex(write, read);
            return read[1];
        }

        internal void WriteRegister(Rfm95wRegister register, byte value)
        {
            Span<byte> write = stackalloc byte[2] { (byte)((byte)register | 0x80), value };
            _spi.Write(write);
        }

        private void WriteFifo(ReadOnlySpan<byte> data)
        {
            Span<byte> write = stackalloc byte[data.Length + 1];
            write[0] = (byte)Rfm95wRegister.Fifo | 0x80;
            data.CopyTo(write.Slice(1));
            _spi.Write(write);
        }

        private void ReadFifo(Span<byte> data)
        {
            Span<byte> write = stackalloc byte[data.Length + 1];
            Span<byte> read = stackalloc byte[data.Length + 1];
            write[0] = (byte)Rfm95wRegister.Fifo;
            _spi.TransferFullDuplex(write, read);
            read.Slice(1).CopyTo(data);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(Rfm95w));
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    if (_receiving && _dio0Pin >= 0)
                    {
                        _gpio.UnregisterCallbackForPinValueChangedEvent(_dio0Pin, OnDio0);
                    }

                    SetMode(OperatingMode.Sleep);
                }
                catch (Exception)
                {
                    // Best effort: the bus may already be gone.
                }

                _receiving = false;
                if (_gpio.IsPinOpen(_resetPin))
                {
                    _gpio.ClosePin(_resetPin);
                }

                if (_dio0Pin >= 0 && _gpio.IsPinOpen(_dio0Pin))
                {
                    _gpio.ClosePin(_dio0Pin);
                }

                if (_shouldDisposeGpio)
                {
                    _gpio.Dispose();
                }

                _spi.Dispose();
            }
        }
    }
}
