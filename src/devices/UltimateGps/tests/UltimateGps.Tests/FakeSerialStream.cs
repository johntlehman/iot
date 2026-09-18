using System.Collections.Concurrent;
using System.Text;

namespace UltimateGps.Tests
{
    /// <summary>
    /// Duplex in-memory serial stream. Reads block until <see cref="Receive"/> queues bytes "from the module";
    /// writes are captured in <see cref="Written"/> and raise <see cref="Sent"/> so a test can answer like the module.
    /// </summary>
    public sealed class FakeSerialStream : Stream
    {
        private readonly BlockingCollection<byte[]> _incoming = new();
        private readonly StringBuilder _written = new();
        private readonly object _lock = new();
        private byte[]? _current;
        private int _currentOffset;
        private bool _disposed;

        /// <summary>Every command line written by the driver, in order.</summary>
        public List<string> Written { get; } = new();

        /// <summary>Raised for each complete line the driver writes.</summary>
        public event Action<string>? Sent;

        public bool Disposed => _disposed;

        /// <summary>Queue text as if the module had transmitted it.</summary>
        public void Receive(string text)
        {
            _incoming.Add(Encoding.ASCII.GetBytes(text));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current == null || _currentOffset >= _current.Length)
            {
                try
                {
                    _current = _incoming.Take();
                }
                catch (Exception) when (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FakeSerialStream));
                }

                _currentOffset = 0;
            }

            int n = Math.Min(count, _current.Length - _currentOffset);
            Array.Copy(_current, _currentOffset, buffer, offset, n);
            _currentOffset += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            List<string> completed = new();
            lock (_lock)
            {
                _written.Append(Encoding.ASCII.GetString(buffer, offset, count));
                string text = _written.ToString();
                int newline;
                while ((newline = text.IndexOf('\n')) >= 0)
                {
                    string line = text.Substring(0, newline).TrimEnd('\r');
                    Written.Add(line);
                    completed.Add(line);
                    text = text.Substring(newline + 1);
                }

                _written.Clear().Append(text);
            }

            foreach (string line in completed)
            {
                Sent?.Invoke(line);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            _incoming.CompleteAdding();
            base.Dispose(disposing);
        }
    }
}
