using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;
using Iot.Device.Nmea0183;
using Iot.Device.Nmea0183.Sentences;
using UnitsNet;

namespace Iot.Device.UltimateGps
{
    /// <summary>
    /// Driver for the Adafruit Ultimate GPS (PA1616S / MTK3339). Reads NMEA sentences from a serial stream, parses
    /// them with Iot.Device.Nmea0183, and sends PMTK configuration commands with acknowledgement tracking.
    /// </summary>
    public sealed class UltimateGps : IDisposable
    {
        /// <summary>Factory-default baud rate.</summary>
        public const int DefaultBaudRate = 9600;

        /// <summary>Baud rate used by <see cref="ConfigureForFlightAsync"/>; fast enough for RMC+GGA at 10 Hz.</summary>
        public const int FlightBaudRate = 57600;

        private readonly Stream _stream;
        private readonly Func<int, bool>? _baudRateChanger;
        private readonly bool _shouldDispose;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<PmtkAck>> _pendingAcks = new();
        private readonly object _writeLock = new();
        private readonly object _fixLock = new();

        private Thread? _readerThread;
        private CancellationTokenSource? _cts;
        private DateTimeOffset _lastMessageTime = DateTimeOffset.UtcNow;
        private TaskCompletionSource<string>? _pendingFirmware;

        private GlobalPositioningSystemFixData? _lastGga;
        private RecommendedMinimumNavigationInformation? _lastRmc;
        private int _sentencesSinceGga;
        private int _sentencesSinceRmc;
        private DateTimeOffset _lastEmittedTime = DateTimeOffset.MinValue;

        /// <summary>
        /// Creates the driver over an already-open stream.
        /// </summary>
        /// <param name="stream">Bidirectional serial stream.</param>
        /// <param name="baudRateChanger">Called when the host side must switch baud rate after PMTK251. Returns
        /// false if unsupported. Null means the host cannot change baud rate.</param>
        /// <param name="shouldDispose">True to dispose the stream with this object.</param>
        public UltimateGps(Stream stream, Func<int, bool>? baudRateChanger = null, bool shouldDispose = true)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _baudRateChanger = baudRateChanger;
            _shouldDispose = shouldDispose;
        }

        /// <summary>
        /// Opens a serial port (for example "/dev/serial0" on a Raspberry Pi) and creates the driver on it.
        /// </summary>
        public static UltimateGps Create(string portName, int baudRate = DefaultBaudRate)
        {
            SerialPort port = new(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                NewLine = "\r\n",
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 1000,
            };
            port.Open();
            port.DiscardInBuffer();

            SerialPortStream stream = new(port);
            return new UltimateGps(stream, baud =>
            {
                port.BaudRate = baud;
                port.DiscardInBuffer();
                return true;
            });
        }

        /// <summary>Raised on the reader thread for every complete line received, before parsing.</summary>
        public event Action<string>? SentenceReceived;

        /// <summary>Raised on the reader thread once per navigation epoch with the merged fix.</summary>
        public event Action<GpsFix>? FixUpdated;

        /// <summary>Raised when a line fails NMEA parsing (bad checksum, garbage after a baud change, ...).</summary>
        public event Action<string, NmeaError>? ParserError;

        /// <summary>The most recent merged fix, or <see cref="GpsFix.Empty"/>.</summary>
        public GpsFix LastFix { get; private set; } = GpsFix.Empty;

        /// <summary>Firmware release string from the last <see cref="QueryFirmwareAsync"/>, if any.</summary>
        public string? FirmwareRelease { get; private set; }

        /// <summary>True while the reader thread is running.</summary>
        public bool IsRunning => _readerThread is { IsAlive: true };

        /// <summary>
        /// Starts the background reader. Must be called before commands can be acknowledged.
        /// </summary>
        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _readerThread = new Thread(() => ReadLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "UltimateGps reader",
            };
            _readerThread.Start();
        }

        /// <summary>
        /// Signals the background reader to stop. A read blocked on the stream only returns once data arrives or
        /// the stream is closed, so the thread may outlive this call; <see cref="Dispose"/> closes the stream first.
        /// </summary>
        public void Stop() => Stop(TimeSpan.FromMilliseconds(250));

        private void Stop(TimeSpan join)
        {
            CancellationTokenSource? cts = _cts;
            Thread? thread = _readerThread;
            if (cts == null || thread == null)
            {
                return;
            }

            cts.Cancel();
            if (thread != Thread.CurrentThread)
            {
                thread.Join(join);
            }

            _readerThread = null;
            _cts = null;
            cts.Dispose();
        }

        /// <summary>
        /// Sends a command without waiting for an acknowledgement.
        /// </summary>
        public void SendCommand(PmtkCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            byte[] bytes = Encoding.ASCII.GetBytes(command.ToWireString());
            lock (_writeLock)
            {
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
        }

        /// <summary>
        /// Sends a command and waits for the matching PMTK001 acknowledgement.
        /// </summary>
        /// <exception cref="TimeoutException">No acknowledgement arrived in time.</exception>
        /// <exception cref="InvalidOperationException">The reader is not running.</exception>
        public async Task<PmtkAck> SendCommandAsync(PmtkCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!IsRunning)
            {
                throw new InvalidOperationException("Call Start() before waiting for acknowledgements");
            }

            TaskCompletionSource<PmtkAck> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[command.Type] = tcs;
            try
            {
                SendCommand(command);
                return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No acknowledgement for {command} within {timeout.TotalMilliseconds:F0} ms");
            }
            finally
            {
                _pendingAcks.TryRemove(new KeyValuePair<int, TaskCompletionSource<PmtkAck>>(command.Type, tcs));
            }
        }

        /// <summary>
        /// Sends PMTK605 and returns the PMTK705 firmware release line.
        /// </summary>
        public async Task<string> QueryFirmwareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!IsRunning)
            {
                throw new InvalidOperationException("Call Start() before querying");
            }

            TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingFirmware = tcs;
            try
            {
                SendCommand(PmtkCommand.QueryFirmware());
                string release = await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                FirmwareRelease = release;
                return release;
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No PMTK705 firmware response within {timeout.TotalMilliseconds:F0} ms");
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingFirmware, null, tcs);
            }
        }

        /// <summary>
        /// Switches the module and the host to a new baud rate. The module does not acknowledge PMTK251, so this
        /// sends the command, waits for it to drain, then reconfigures the host port.
        /// </summary>
        /// <exception cref="NotSupportedException">The stream was created without a baud rate changer.</exception>
        public async Task SetBaudRateAsync(int baud, CancellationToken cancellationToken = default)
        {
            if (_baudRateChanger == null)
            {
                throw new NotSupportedException("This stream cannot change baud rate");
            }

            SendCommand(PmtkCommand.SetBaudRate(baud));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (!_baudRateChanger(baud))
            {
                throw new NotSupportedException($"Host could not switch to {baud} baud");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Configures the module for flight: 57600 baud, RMC+GGA only, 5 Hz fix rate (the chip maximum) and 10 Hz
        /// NMEA output. Each step waits for its acknowledgement.
        /// </summary>
        /// <param name="nmeaRateHz">NMEA output rate, 1..10 Hz.</param>
        /// <param name="fixRateHz">Fix rate, 1..5 Hz.</param>
        /// <param name="output">Sentences to enable.</param>
        /// <param name="baud">Baud rate to switch to; 0 keeps the current rate.</param>
        /// <param name="ackTimeout">Per-command acknowledgement timeout (default 2 s).</param>
        public async Task ConfigureForFlightAsync(
            int nmeaRateHz = 10,
            int fixRateHz = 5,
            NmeaOutput output = NmeaOutput.RmcGga,
            int baud = FlightBaudRate,
            TimeSpan? ackTimeout = null,
            CancellationToken cancellationToken = default)
        {
            TimeSpan timeout = ackTimeout ?? TimeSpan.FromSeconds(2);

            if (baud != 0)
            {
                await SetBaudRateAsync(baud, cancellationToken).ConfigureAwait(false);
            }

            await SendCheckedAsync(PmtkCommand.SetNmeaOutput(output), timeout, cancellationToken).ConfigureAwait(false);
            await SendCheckedAsync(PmtkCommand.SetFixRate(fixRateHz), timeout, cancellationToken).ConfigureAwait(false);
            await SendCheckedAsync(PmtkCommand.SetNmeaUpdateRate(nmeaRateHz), timeout, cancellationToken).ConfigureAwait(false);
        }

        private async Task SendCheckedAsync(PmtkCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            PmtkAck ack = await SendCommandAsync(command, timeout, cancellationToken).ConfigureAwait(false);
            if (!ack.IsSuccess)
            {
                throw new IOException($"Module rejected {command}: {ack.Flag}");
            }
        }

        /// <summary>
        /// Feeds one received line through the parser. Public so that logged NMEA can be replayed in tests or tools.
        /// </summary>
        public void ProcessLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            line = line.Trim();
            SentenceReceived?.Invoke(line);

            if (line.StartsWith("$PMTK", StringComparison.Ordinal) || line.StartsWith("$PGTOP", StringComparison.Ordinal) || line.StartsWith("$PGACK", StringComparison.Ordinal))
            {
                ProcessProprietary(line);
                return;
            }

            TalkerSentence? talker = TalkerSentence.FromSentenceString(line, out NmeaError error);
            if (talker == null)
            {
                if (error != NmeaError.None)
                {
                    ParserError?.Invoke(line, error);
                }

                return;
            }

            NmeaSentence? sentence = talker.TryGetTypedValue(ref _lastMessageTime);
            switch (sentence)
            {
                case GlobalPositioningSystemFixData gga:
                    OnGga(gga);
                    break;
                case RecommendedMinimumNavigationInformation rmc:
                    OnRmc(rmc);
                    break;
                default:
                    CountOther();
                    break;
            }
        }

        private void ProcessProprietary(string line)
        {
            if (PmtkAck.TryParse(line, out PmtkAck? ack) && ack != null)
            {
                if (_pendingAcks.TryGetValue(ack.CommandType, out TaskCompletionSource<PmtkAck>? tcs))
                {
                    tcs.TrySetResult(ack);
                }

                return;
            }

            if (line.StartsWith("$PMTK705,", StringComparison.Ordinal))
            {
                int star = line.IndexOf('*');
                string payload = star > 0 ? line.Substring(9, star - 9) : line.Substring(9);
                _pendingFirmware?.TrySetResult(payload);
            }
        }

        private void OnGga(GlobalPositioningSystemFixData gga)
        {
            lock (_fixLock)
            {
                _lastGga = gga;
                _sentencesSinceGga = 0;
                _sentencesSinceRmc = Saturate(_sentencesSinceRmc);
                MergeAndMaybeEmit(gga.DateTime);
            }
        }

        private void OnRmc(RecommendedMinimumNavigationInformation rmc)
        {
            lock (_fixLock)
            {
                _lastRmc = rmc;
                _sentencesSinceRmc = 0;
                _sentencesSinceGga = Saturate(_sentencesSinceGga);
                MergeAndMaybeEmit(rmc.DateTime);
            }
        }

        private void CountOther()
        {
            lock (_fixLock)
            {
                _sentencesSinceGga = Saturate(_sentencesSinceGga);
                _sentencesSinceRmc = Saturate(_sentencesSinceRmc);
            }
        }

        private static int Saturate(int value) => value == int.MaxValue ? value : value + 1;

        /// <summary>
        /// Emit once per epoch: when GGA and RMC carry the same time of day, or when only one of them is enabled
        /// (the other has not been seen for a while).
        /// </summary>
        private void MergeAndMaybeEmit(DateTimeOffset sentenceTime)
        {
            const int AbsentThreshold = 4;

            bool ggaAbsent = _sentencesSinceGga >= AbsentThreshold;
            bool rmcAbsent = _sentencesSinceRmc >= AbsentThreshold;
            bool sameEpoch = _lastGga != null && _lastRmc != null &&
                             _lastGga.DateTime.TimeOfDay == _lastRmc.DateTime.TimeOfDay;

            if (!(sameEpoch || ggaAbsent || rmcAbsent))
            {
                return;
            }

            if (sentenceTime == _lastEmittedTime && sameEpoch)
            {
                return;
            }

            GlobalPositioningSystemFixData? gga = _lastGga;
            RecommendedMinimumNavigationInformation? rmc = _lastRmc;
            bool useGga = gga != null && (sameEpoch || !ggaAbsent || rmc == null);
            bool useRmc = rmc != null && (sameEpoch || !rmcAbsent || gga == null);

            GpsFix fix = LastFix;

            if (useGga && gga != null)
            {
                fix = fix with
                {
                    Time = gga.DateTime,
                    Quality = gga.Status,
                    HasFix = gga.Status != GpsQuality.NoFix && gga.Valid,
                    Latitude = gga.Valid ? gga.Position.Latitude : fix.Latitude,
                    Longitude = gga.Valid ? gga.Position.Longitude : fix.Longitude,
                    Altitude = gga.GeoidAltitude.HasValue ? Length.FromMeters(gga.GeoidAltitude.Value) : fix.Altitude,
                    SatellitesInUse = gga.NumberOfSatellites,
                    Hdop = gga.Hdop,
                };
            }

            if (useRmc && rmc != null)
            {
                bool rmcValid = rmc.Valid && rmc.Status == NavigationStatus.Valid;
                fix = fix with
                {
                    Time = rmc.DateTime,
                    HasFix = useGga && gga != null ? fix.HasFix : rmcValid,
                    Latitude = rmc.Valid ? rmc.Position.Latitude : fix.Latitude,
                    Longitude = rmc.Valid ? rmc.Position.Longitude : fix.Longitude,
                    SpeedOverGround = rmc.SpeedOverGround,
                    Course = rmc.TrackMadeGoodInDegreesTrue,
                };
            }

            _lastEmittedTime = sentenceTime;
            LastFix = fix;
            FixUpdated?.Invoke(fix);
        }

        private void ReadLoop(CancellationToken token)
        {
            byte[] buffer = new byte[512];
            StringBuilder line = new(128);

            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = _stream.Read(buffer, 0, buffer.Length);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(50);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (read == 0)
                {
                    Thread.Sleep(10);
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    char c = (char)buffer[i];
                    if (c == '\n')
                    {
                        string text = line.ToString();
                        line.Clear();
                        try
                        {
                            ProcessLine(text);
                        }
                        catch (Exception)
                        {
                            // A malformed sentence must not kill the reader.
                        }
                    }
                    else if (c != '\r')
                    {
                        if (line.Length < 512)
                        {
                            line.Append(c);
                        }
                        else
                        {
                            line.Clear(); // garbage; resync on the next newline
                        }
                    }
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _cts?.Cancel();
            foreach (TaskCompletionSource<PmtkAck> tcs in _pendingAcks.Values)
            {
                tcs.TrySetCanceled();
            }

            if (_shouldDispose)
            {
                _stream.Dispose(); // unblocks a pending Read so the reader can exit
                Stop(TimeSpan.FromSeconds(2));
            }
            else
            {
                Stop();
            }
        }

        /// <summary>
        /// Wraps a SerialPort so that disposing the stream closes the port, and reads are unblocked by Close().
        /// </summary>
        private sealed class SerialPortStream : Stream
        {
            private readonly SerialPort _port;

            public SerialPortStream(SerialPort port)
            {
                _port = port;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() => _port.BaseStream.Flush();

            public override int Read(byte[] buffer, int offset, int count) => _port.BaseStream.Read(buffer, offset, count);

            public override void Write(byte[] buffer, int offset, int count) => _port.BaseStream.Write(buffer, offset, count);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _port.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}
