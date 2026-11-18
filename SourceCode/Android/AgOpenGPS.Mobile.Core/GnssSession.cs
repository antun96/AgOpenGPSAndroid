using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgLibrary.Ntrip;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>
    /// One connection to the GNSS device (Bluetooth SPP socket, USB serial, TCP - anything that is a
    /// pair of streams). Reads and decodes on a background thread, keeps the latest fix, and lets
    /// corrections be written back. Platform code only has to open the streams.
    /// </summary>
    public sealed class GnssSession
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly Func<DateTime> _utcNow;
        private readonly GnssStreamDecoder _decoder = new GnssStreamDecoder();
        private readonly NmeaAccumulator _nmea = new NmeaAccumulator();
        private readonly object _writeLock = new object();
        private readonly object _stateLock = new object();
        private readonly Queue<DateTime> _fixTimes = new Queue<DateTime>();

        private GnssFix _latest;
        private long _bytesIn, _bytesOut, _sentences, _badSentences, _pgnFrames;
        private DateTime _lastDataUtc;

        /// <summary>Raised on the reader thread for every completed fix.</summary>
        public event Action<GnssFix> FixUpdated;

        /// <summary>Raised on the reader thread for every NMEA line (valid or not), for a raw log.</summary>
        public event Action<string> SentenceReceived;

        /// <summary>Raised on the reader thread for every PGN 253 from a steer module.</summary>
        public event Action<SteerFeedback> SteerFeedbackReceived;

        private readonly RtcmFrameTracker _rtcm = new RtcmFrameTracker();
        private readonly List<byte[]> _pendingFrames = new List<byte[]>();
        private long _framesOut;

        public GnssSession(Stream input, Stream output, Func<DateTime> utcNow = null)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _decoder.SentenceReceived += OnSentence;
            _decoder.PgnReceived += OnPgn;
        }

        /// <summary>Reads until the stream ends, fails, or <paramref name="ct"/> is cancelled.
        /// Cancelling cannot interrupt a blocking Bluetooth read: close the socket as well.</summary>
        public Task RunAsync(CancellationToken ct)
        {
            return Task.Factory.StartNew(() => ReadLoop(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ReadLoop(CancellationToken ct)
        {
            var buffer = new byte[2048];
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = _input.Read(buffer, 0, buffer.Length);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }

                if (n <= 0) return; // the other side closed the link
                Interlocked.Add(ref _bytesIn, n);
                lock (_stateLock) _lastDataUtc = _utcNow();
                _decoder.Feed(buffer, 0, n);
            }
        }

        private void OnSentence(string line)
        {
            SentenceReceived?.Invoke(line);
            if (!Nmea.ChecksumOk(line))
            {
                Interlocked.Increment(ref _badSentences);
                return;
            }
            Interlocked.Increment(ref _sentences);
            Publish(_nmea.Process(line, _utcNow()));
        }

        private void OnPgn(byte[] frame)
        {
            Interlocked.Increment(ref _pgnFrames);
            if (frame[3] == SteerPgn.Feedback)
            {
                SteerFeedback fb = SteerPgn.ParseFeedback(frame, _utcNow());
                if (fb != null) SteerFeedbackReceived?.Invoke(fb);
                return;
            }
            Publish(Pgn.ParseGpsPosition(frame, _utcNow()));
        }

        private void Publish(GnssFix fix)
        {
            if (fix == null) return;
            lock (_stateLock)
            {
                _latest = fix;
                _fixTimes.Enqueue(fix.ReceivedUtc);
                while (_fixTimes.Count > 0 && (fix.ReceivedUtc - _fixTimes.Peek()).TotalSeconds > 3) _fixTimes.Dequeue();
            }
            FixUpdated?.Invoke(fix);
        }

        /// <summary>Writes bytes to the device (RTCM corrections). Thread safe. Returns false if the link failed.</summary>
        public bool Write(byte[] data, int offset, int count)
        {
            if (_output == null || count <= 0) return false;
            try
            {
                lock (_writeLock)
                {
                    _output.Write(data, offset, count);
                    _output.Flush();
                }
                Interlocked.Add(ref _bytesOut, count);
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>
        /// Writes RTCM corrections. Their frames are tracked, so PGN frames from <see cref="WriteFrame"/>
        /// go in only between two RTCM frames: the bridge on the other end (ESP32) splits the stream
        /// by the same rule, RTCM to the receiver and PGN to the steer module.
        /// </summary>
        public bool WriteCorrections(byte[] data, int offset, int count)
        {
            if (_output == null || count <= 0) return false;
            try
            {
                lock (_writeLock)
                {
                    int start = offset, end = offset + count;
                    for (int i = offset; i < end; i++)
                    {
                        if (_rtcm.Feed(data[i]) && _pendingFrames.Count > 0)
                        {
                            // A frame boundary with steer frames waiting: send up to here, then them.
                            _output.Write(data, start, i + 1 - start);
                            start = i + 1;
                            WritePendingLocked();
                        }
                    }
                    if (end > start) _output.Write(data, start, end - start);
                    if (_rtcm.AtBoundary) WritePendingLocked();
                    _output.Flush();
                }
                Interlocked.Add(ref _bytesOut, count);
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        /// <summary>
        /// Writes one PGN frame (to the steer module), now if the correction stream is between frames,
        /// else as soon as the current RTCM frame is complete. A newer frame of the same PGN waiting
        /// replaces the older one. Returns false if the link failed.
        /// </summary>
        public bool WriteFrame(byte[] frame)
        {
            if (_output == null || frame == null || frame.Length < 5) return false;
            try
            {
                lock (_writeLock)
                {
                    _pendingFrames.RemoveAll(f => f[3] == frame[3]);
                    _pendingFrames.Add(frame);
                    if (_rtcm.AtBoundary)
                    {
                        WritePendingLocked();
                        _output.Flush();
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        private void WritePendingLocked()
        {
            foreach (byte[] f in _pendingFrames)
            {
                _output.Write(f, 0, f.Length);
                Interlocked.Add(ref _bytesOut, f.Length);
                Interlocked.Increment(ref _framesOut);
            }
            _pendingFrames.Clear();
        }

        /// <summary>A GGA sentence with CR/LF for the NTRIP caster: the receiver's own when it sends GGA,
        /// else one built from the latest fix. Null while there is no recent fix.</summary>
        public string GgaForCaster()
        {
            GnssFix fix;
            lock (_stateLock) fix = _latest;
            if (fix == null || !fix.HasPosition || (_utcNow() - fix.ReceivedUtc).TotalSeconds > 5) return null;

            // The NTRIP client writes this as is, and casters wait for the line end.
            string gga = fix.Source == FixSource.Nmea ? _nmea.LastGga : null;
            if (gga != null) return gga + "\r\n";

            return GgaBuilder.FromReceiver(_utcNow(), fix.Latitude, fix.Longitude, fix.FixQuality,
                Math.Max(0, fix.Satellites), double.IsNaN(fix.Hdop) ? 1.0 : fix.Hdop,
                double.IsNaN(fix.Altitude) ? 0 : fix.Altitude, double.IsNaN(fix.AgeSeconds) ? 0 : fix.AgeSeconds);
        }

        public SessionStats GetStats()
        {
            lock (_stateLock)
            {
                DateTime now = _utcNow();
                double rate = 0;
                if (_fixTimes.Count >= 2)
                {
                    // Over the stored window; a link that went quiet shows 0 via SecondsSinceFix instead.
                    double span = (_latest.ReceivedUtc - _fixTimes.Peek()).TotalSeconds;
                    if (span > 0.2) rate = (_fixTimes.Count - 1) / span;
                }

                return new SessionStats
                {
                    Latest = _latest?.Clone(),
                    BytesIn = Interlocked.Read(ref _bytesIn),
                    BytesOut = Interlocked.Read(ref _bytesOut),
                    Sentences = Interlocked.Read(ref _sentences),
                    BadSentences = Interlocked.Read(ref _badSentences),
                    PgnFrames = Interlocked.Read(ref _pgnFrames),
                    PgnFramesOut = Interlocked.Read(ref _framesOut),
                    SkippedBytes = _decoder.SkippedBytes,
                    RateHz = rate,
                    SecondsSinceData = _lastDataUtc == default ? -1 : (now - _lastDataUtc).TotalSeconds,
                    SecondsSinceFix = _latest == null ? -1 : (now - _latest.ReceivedUtc).TotalSeconds,
                };
            }
        }
    }

    public sealed class SessionStats
    {
        public GnssFix Latest;
        public long BytesIn, BytesOut, Sentences, BadSentences, PgnFrames, SkippedBytes;
        /// <summary>PGN frames sent to the device (steer module).</summary>
        public long PgnFramesOut;
        /// <summary>Fixes per second over the last 3 s.</summary>
        public double RateHz;
        public double SecondsSinceData;
        public double SecondsSinceFix;
    }

    /// <summary>
    /// Follows a byte stream of RTCM 3 corrections and tells where whole frames end: 0xD3, 6 reserved
    /// bits and a 10-bit length, the payload, then a 3-byte CRC. Bytes outside frames (anything that
    /// does not start with 0xD3) count as complete on their own. The ESP32 bridge uses the same rule.
    /// </summary>
    public sealed class RtcmFrameTracker
    {
        public const byte Preamble = 0xD3;
        private int _pos;     // bytes of the current frame seen; 0 = between frames
        private int _total;   // whole frame length once known

        public bool AtBoundary => _pos == 0;

        /// <summary>Feeds one byte; true when the stream is between frames after it.</summary>
        public bool Feed(byte b)
        {
            if (_pos == 0)
            {
                if (b != Preamble) return true;
                _pos = 1;
                _total = 0;
                return false;
            }
            _pos++;
            if (_pos == 2) _total = (b & 0x03) << 8;
            else if (_pos == 3) _total = 3 + (_total | b) + 3;
            else if (_pos >= _total) { _pos = 0; return true; }
            return false;
        }
    }
}
