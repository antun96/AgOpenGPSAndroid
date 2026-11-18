using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>
    /// A virtual tractor with an RTK fix, for trying the app without a receiver. Bicycle model:
    /// the heading turns by speed / wheelbase * tan(wheel angle). Produces GGA + VTG like a real
    /// receiver, so everything after the link works unchanged. Thread safe.
    /// </summary>
    public sealed class GnssSimulator
    {
        public const double MaxWheelAngle = 35;
        public const double MaxSpeedKmh = 30;

        private readonly object _lock = new object();
        private double _lat, _lon, _heading, _speed, _wheel;

        public GnssSimulator(double latitude, double longitude, double headingDeg = 0)
        {
            _lat = latitude;
            _lon = longitude;
            _heading = LocalPlane.Wrap360(headingDeg);
        }

        public double Wheelbase { get; set; } = 3.0;

        /// <summary>Puts the tractor somewhere else (e.g. into the field just opened), stopped, wheel straight.</summary>
        public void MoveTo(double latitude, double longitude)
        {
            lock (_lock)
            {
                _lat = latitude;
                _lon = longitude;
                _speed = 0;
                _wheel = 0;
            }
            AutoSteer = false;
        }

        /// <summary>Steers itself along the active line (pure pursuit), like AutoSteer would.</summary>
        public bool AutoSteer { get; set; }

        /// <summary>Where to steer to while <see cref="AutoSteer"/> is on; null = hold the wheel.</summary>
        public Func<SteerResult> SteerSource { get; set; }

        public double Latitude { get { lock (_lock) return _lat; } }
        public double Longitude { get { lock (_lock) return _lon; } }
        public double Heading { get { lock (_lock) return _heading; } }

        public double SpeedKmh
        {
            get { lock (_lock) return _speed; }
            set { lock (_lock) _speed = Math.Max(-10, Math.Min(MaxSpeedKmh, value)); }
        }

        /// <summary>Front wheel angle in degrees, positive = right.</summary>
        public double WheelAngle
        {
            get { lock (_lock) return _wheel; }
            set { lock (_lock) _wheel = Math.Max(-MaxWheelAngle, Math.Min(MaxWheelAngle, value)); }
        }

        /// <summary>The simulated steer module (answers the app's PGN 254 like an ESP32 steer module would).</summary>
        public SimSteerModule SteerModule { get; } = new SimSteerModule();

        /// <summary>
        /// The driver turns the wheel by hand. While the steer module steers this counts as taking
        /// over, as on a real tractor: the module stops and reports its steer switch off.
        /// </summary>
        public void DriverTurnsWheel(double angle)
        {
            if (SteerModule.IsSteering(DateTime.UtcNow)) SteerModule.Override();
            WheelAngle = angle;
        }

        /// <summary>Moves the tractor on by <paramref name="seconds"/>.</summary>
        public void Step(double seconds)
        {
            DateTime now = DateTime.UtcNow;
            if (SteerModule.IsSteering(now))
            {
                WheelAngle = SteerModule.Step(WheelAngle, seconds, now);
            }
            else if (AutoSteer)
            {
                SteerResult target = SteerSource?.Invoke();
                if (target != null) WheelAngle = Steering.PurePursuitWheelAngle(target.SteerDegrees, target.LookaheadMeters, Wheelbase);
            }

            lock (_lock)
            {
                double v = _speed / 3.6, dist = v * seconds;
                _heading = LocalPlane.Wrap360(_heading + LocalPlane.ToDeg(dist / Wheelbase * Math.Tan(LocalPlane.ToRad(_wheel))));
                var plane = new LocalPlane(_lat, _lon);
                double h = LocalPlane.ToRad(_heading);
                plane.ToWgs84(new GeoPoint(Math.Cos(h) * dist, Math.Sin(h) * dist), out _lat, out _lon);
            }
        }

        /// <summary>GGA + VTG for the current state (RTK fix, 14 satellites), with CR/LF.</summary>
        public string Sentences(DateTime utc)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            double lat, lon, heading, speed;
            lock (_lock) { lat = _lat; lon = _lon; heading = _heading; speed = _speed; }

            string gga = string.Format(ci, "$GNGGA,{0},{1},{2},4,14,0.7,120.0,M,45.0,M,1.0,0000",
                utc.ToString("HHmmss.00", ci), Coord(lat, 2, 'N', 'S'), Coord(lon, 3, 'E', 'W'));
            double cog = speed < 0 ? LocalPlane.Wrap360(heading + 180) : heading;
            string vtg = string.Format(ci, "$GNVTG,{0:0.00},T,,M,{1:0.000},N,{2:0.000},K,R", cog, Math.Abs(speed) / 1.852, Math.Abs(speed));
            return gga + "*" + Nmea.Checksum(gga) + "\r\n" + vtg + "*" + Nmea.Checksum(vtg) + "\r\n";
        }

        private static string Coord(double value, int degDigits, char pos, char neg)
        {
            double a = Math.Abs(value);
            int deg = (int)a;
            double min = (a - deg) * 60.0;
            string d = deg.ToString(new string('0', degDigits), CultureInfo.InvariantCulture);
            return d + min.ToString("00.0000000", CultureInfo.InvariantCulture) + "," + (value >= 0 ? pos : neg);
        }
    }

    /// <summary>A <see cref="IGnssLink"/> fed by a <see cref="GnssSimulator"/> at a fixed rate.</summary>
    public sealed class SimulatorLink : IGnssLink
    {
        public SimulatorLink(GnssSimulator simulator, int hz = 10)
        {
            Input = new SimStream(simulator, hz);
            Output = new ModuleInput(simulator.SteerModule);
        }

        public Stream Input { get; }
        /// <summary>Corrections go nowhere; PGN frames go to the simulated steer module.</summary>
        public Stream Output { get; }

        public void Dispose() => Input.Dispose();

        private sealed class SimStream : Stream
        {
            private readonly GnssSimulator _sim;
            private readonly int _periodMs;
            private readonly ManualResetEventSlim _closed = new ManualResetEventSlim(false);
            private byte[] _pending = Array.Empty<byte>();
            private int _pos;
            private DateTime _next = DateTime.UtcNow;

            public SimStream(GnssSimulator sim, int hz)
            {
                _sim = sim;
                _periodMs = 1000 / Math.Max(1, hz);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _pending.Length)
                {
                    TimeSpan wait = _next - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero && _closed.Wait(wait)) return 0;
                    if (_closed.IsSet) return 0;
                    _next = (wait > TimeSpan.Zero ? _next : DateTime.UtcNow).AddMilliseconds(_periodMs);
                    _sim.Step(_periodMs / 1000.0);
                    byte[] nmea = Encoding.ASCII.GetBytes(_sim.Sentences(DateTime.UtcNow));
                    byte[] steer = _sim.SteerModule.Feedback(_sim.WheelAngle);
                    if (steer == null) _pending = nmea;
                    else
                    {
                        _pending = new byte[nmea.Length + steer.Length];
                        nmea.CopyTo(_pending, 0);
                        steer.CopyTo(_pending, nmea.Length);
                    }
                    _pos = 0;
                }
                int n = Math.Min(count, _pending.Length - _pos);
                Array.Copy(_pending, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            protected override void Dispose(bool disposing)
            {
                _closed.Set();
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }

        /// <summary>What the app writes to the link: PGN frames are picked out and given to the module.</summary>
        private sealed class ModuleInput : Stream
        {
            private readonly GnssStreamDecoder _decoder = new GnssStreamDecoder();

            public ModuleInput(SimSteerModule module)
            {
                _decoder.PgnReceived += f => module.Receive(f, DateTime.UtcNow);
            }

            public override void Write(byte[] b, int o, int c) => _decoder.Feed(b, o, c);
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A simulated ESP32 steer module with an electric motor on the steering wheel: takes PGN 254
    /// (and 252 settings), turns the wheel toward the wanted angle with the same motor control as
    /// the firmware, and answers with PGN 253. It answers only once the app has talked to it, so it
    /// stays silent when AutoSteer is not in use. Thread safe.
    /// </summary>
    public sealed class SimSteerModule
    {
        /// <summary>Wheel speed at full PWM, degrees per second.</summary>
        public const double FullPwmDegPerSec = 40;
        /// <summary>Without a PGN 254 for this long the module stops steering, like the firmware's watchdog.</summary>
        public const double WatchdogSeconds = 1.0;

        private readonly object _lock = new object();
        private readonly SteerSettings _settings = new SteerSettings();
        private DateTime _lastCommand = DateTime.MinValue;
        private bool _heard, _on, _override;
        private double _setpoint, _speed;
        private int _pwm;

        public void Receive(byte[] frame, DateTime utc)
        {
            lock (_lock)
            {
                if (SteerPgn.ParseAutoSteerData(frame, out double speed, out bool on, out double angle))
                {
                    _heard = true;
                    _lastCommand = utc;
                    _on = on;
                    _setpoint = angle;
                    _speed = speed;
                    if (!on) _override = false; // the driver took over; cleared once the app switched off
                }
                else if (SteerPgn.ParseSettings(frame, _settings))
                {
                    _heard = true;
                }
            }
        }

        /// <summary>Driver took the wheel: stop and report the steer switch off.</summary>
        public void Override()
        {
            lock (_lock) _override = true;
        }

        public bool IsSteering(DateTime utc)
        {
            lock (_lock)
                return _on && !_override && (utc - _lastCommand).TotalSeconds < WatchdogSeconds && _speed >= 0.1;
        }

        /// <summary>The wheel angle after <paramref name="seconds"/> of motor drive.</summary>
        public double Step(double wheel, double seconds, DateTime utc)
        {
            lock (_lock)
            {
                // Small steps, as the firmware runs its loop at 100 Hz.
                int n = Math.Max(1, (int)Math.Ceiling(seconds / 0.01));
                double dt = seconds / n;
                for (int k = 0; k < n; k++)
                {
                    _pwm = SteerPid.Pwm(_setpoint, wheel, _settings);
                    int drive = _settings.InvertMotor ? -_pwm : _pwm;
                    wheel -= drive / 255.0 * FullPwmDegPerSec * dt;
                }
                return wheel;
            }
        }

        /// <summary>PGN 253 for the current wheel angle, or null while the app has not talked to the module.</summary>
        public byte[] Feedback(double wheel)
        {
            lock (_lock)
            {
                if (!_heard) return null;
                bool steering = _on && !_override && (DateTime.UtcNow - _lastCommand).TotalSeconds < WatchdogSeconds;
                return SteerPgn.BuildFeedback(wheel, false, _override, steering ? _pwm : 0);
            }
        }
    }
}
