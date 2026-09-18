using System.Device.I2c;

namespace Mpu6050.Tests
{
    /// <summary>
    /// In-memory I2C device that behaves like a register-mapped sensor: the first byte of a write selects the
    /// register pointer, remaining bytes are written sequentially, and reads return from the pointer with auto-increment.
    /// Every register write is recorded in <see cref="Writes"/> for assertions.
    /// </summary>
    public sealed class FakeI2cDevice : I2cDevice
    {
        private readonly byte[] _registers = new byte[256];
        private int _pointer;

        public FakeI2cDevice(int busId = 1, int address = 0x68)
        {
            ConnectionSettings = new I2cConnectionSettings(busId, address);
        }

        public override I2cConnectionSettings ConnectionSettings { get; }

        /// <summary>(register, value) pairs in the order they were written.</summary>
        public List<(byte Register, byte Value)> Writes { get; } = new();

        /// <summary>(start register, length) of every multi-byte read.</summary>
        public List<(byte Register, int Length)> ReadLog { get; } = new();

        public bool Disposed { get; private set; }

        public byte this[byte register]
        {
            get => _registers[register];
            set => _registers[register] = value;
        }

        public override byte ReadByte()
        {
            byte value = _registers[_pointer];
            _pointer = (_pointer + 1) & 0xFF;
            return value;
        }

        public override void Read(Span<byte> buffer)
        {
            ReadLog.Add(((byte)_pointer, buffer.Length));
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = ReadByte();
            }
        }

        public override void WriteByte(byte value)
        {
            _pointer = value;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            _pointer = buffer[0];
            for (int i = 1; i < buffer.Length; i++)
            {
                _registers[_pointer] = buffer[i];
                Writes.Add(((byte)_pointer, buffer[i]));
                _pointer = (_pointer + 1) & 0xFF;
            }
        }

        public override void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            Write(writeBuffer);
            Read(readBuffer);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
