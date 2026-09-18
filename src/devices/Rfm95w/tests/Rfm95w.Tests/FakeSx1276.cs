using System.Device.Spi;
using Iot.Device.Rfm95w;

namespace Rfm95w.Tests
{
    /// <summary>
    /// In-memory SX1276 behind an SpiDevice: a register file with LoRa power-on defaults, a 256-byte FIFO with the
    /// address-pointer auto-increment, write-1-to-clear IRQ flags, and an instant TxDone when TX mode is entered.
    /// </summary>
    public sealed class FakeSx1276 : SpiDevice
    {
        private const byte RegFifo = 0x00;
        private const byte RegOpMode = 0x01;
        private const byte RegFifoAddrPtr = 0x0D;
        private const byte RegFifoRxBaseAddr = 0x0F;
        private const byte RegFifoRxCurrentAddr = 0x10;
        private const byte RegIrqFlags = 0x12;
        private const byte RegRxNbBytes = 0x13;
        private const byte RegPktSnrValue = 0x19;
        private const byte RegPktRssiValue = 0x1A;

        private readonly byte[] _registers = new byte[0x80];

        public FakeSx1276()
        {
            ConnectionSettings = Iot.Device.Rfm95w.Rfm95w.GetDefaultSpiSettings();

            // Power-on defaults from the datasheet register table.
            _registers[RegOpMode] = 0x01;      // FSK standby
            _registers[0x06] = 0x6C;           // 434 MHz
            _registers[0x07] = 0x80;
            _registers[0x08] = 0x00;
            _registers[0x09] = 0x4F;           // PaConfig
            _registers[0x0B] = 0x2B;           // Ocp
            _registers[0x0C] = 0x20;           // Lna
            _registers[0x0E] = 0x80;           // FifoTxBaseAddr
            _registers[RegFifoRxBaseAddr] = 0x00;
            _registers[0x1D] = 0x72;           // ModemConfig1: 125 kHz, 4/5, explicit
            _registers[0x1E] = 0x70;           // ModemConfig2: SF7
            _registers[0x21] = 0x08;           // PreambleLsb
            _registers[0x23] = 0xFF;           // MaxPayloadLength
            _registers[0x31] = 0xC3;           // DetectOptimize
            _registers[0x37] = 0x0A;           // DetectionThreshold
            _registers[0x39] = 0x12;           // SyncWord
            _registers[0x42] = 0x12;           // Version
            _registers[0x4D] = 0x84;           // PaDac
        }

        public override SpiConnectionSettings ConnectionSettings { get; }

        public byte[] Fifo { get; } = new byte[256];

        /// <summary>Every register write except FIFO data, in order.</summary>
        public List<(byte Register, byte Value)> Writes { get; } = new();

        /// <summary>Every value written to RegOpMode, in order.</summary>
        public List<byte> OpModeWrites { get; } = new();

        /// <summary>When true (default) entering TX sets TxDone immediately, as a real chip would after the airtime.</summary>
        public bool AutoTxDone { get; set; } = true;

        public bool Disposed { get; private set; }

        public byte this[byte register]
        {
            get => _registers[register];
            set => _registers[register] = value;
        }

        internal byte this[Rfm95wRegister register]
        {
            get => _registers[(byte)register];
            set => _registers[(byte)register] = value;
        }

        /// <summary>
        /// Simulates a received packet: places it in the FIFO at the RX base address and raises RxDone.
        /// </summary>
        public void InjectPacket(byte[] payload, sbyte rawSnr = 40, byte rawRssi = 100, bool crcError = false)
        {
            byte rxBase = _registers[RegFifoRxBaseAddr];
            Array.Copy(payload, 0, Fifo, rxBase, payload.Length);
            _registers[RegFifoRxCurrentAddr] = rxBase;
            _registers[RegRxNbBytes] = (byte)payload.Length;
            _registers[RegPktSnrValue] = (byte)rawSnr;
            _registers[RegPktRssiValue] = rawRssi;
            _registers[RegIrqFlags] |= 0x40; // RxDone
            if (crcError)
            {
                _registers[RegIrqFlags] |= 0x20; // PayloadCrcError
            }
        }

        public override void Read(Span<byte> buffer) => throw new NotSupportedException("The driver uses TransferFullDuplex for reads");

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Span<byte> discard = stackalloc byte[buffer.Length];
            TransferFullDuplex(buffer, discard);
        }

        public override void TransferFullDuplex(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            if (writeBuffer.Length == 0)
            {
                return;
            }

            byte address = (byte)(writeBuffer[0] & 0x7F);
            bool isWrite = (writeBuffer[0] & 0x80) != 0;
            readBuffer[0] = 0;

            for (int i = 1; i < writeBuffer.Length; i++)
            {
                if (isWrite)
                {
                    WriteRegister(address, writeBuffer[i]);
                    readBuffer[i] = 0;
                }
                else
                {
                    readBuffer[i] = ReadRegister(address);
                }
            }
        }

        private byte ReadRegister(byte address)
        {
            if (address == RegFifo)
            {
                byte value = Fifo[_registers[RegFifoAddrPtr]];
                _registers[RegFifoAddrPtr]++;
                return value;
            }

            return _registers[address];
        }

        private void WriteRegister(byte address, byte value)
        {
            switch (address)
            {
                case RegFifo:
                    Fifo[_registers[RegFifoAddrPtr]] = value;
                    _registers[RegFifoAddrPtr]++;
                    return;
                case RegIrqFlags:
                    _registers[RegIrqFlags] &= (byte)~value; // write 1 to clear
                    break;
                case RegOpMode:
                    _registers[RegOpMode] = value;
                    OpModeWrites.Add(value);
                    if ((value & 0x07) == 0x03 && AutoTxDone)
                    {
                        _registers[RegIrqFlags] |= 0x08; // TxDone
                        _registers[RegOpMode] = (byte)((value & 0xF8) | 0x01); // chip drops back to standby
                    }

                    break;
                default:
                    _registers[address] = value;
                    break;
            }

            Writes.Add((address, value));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
