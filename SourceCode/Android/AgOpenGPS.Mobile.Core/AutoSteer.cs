using System;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Mobile.Core
{
    public enum SteerSwitchType
    {
        /// <summary>No switch: steering is switched on and off on the tablet only.</summary>
        None,
        /// <summary>A switch that is on while steering (AgOpenGPS "Switch").</summary>
        Switch,
        /// <summary>A push button that toggles steering (AgOpenGPS "Button").</summary>
        Button,
    }

    /// <summary>Why AutoSteer is off, or cannot be switched on.</summary>
    public enum SteerOffReason
    {
        None,
        /// <summary>Switched off on the tablet.</summary>
        UserOff,
        /// <summary>No connection to the receiver / steer module.</summary>
        LinkLost,
        /// <summary>No recent position.</summary>
        NoPosition,
        /// <summary>No RTK fix (or it was lost for too long).</summary>
        NoRtk,
        /// <summary>No guidance line, or no heading yet.</summary>
        NoLine,
        /// <summary>Too far from the line.</summary>
        OffLine,
        /// <summary>Faster than the maximum steering speed.</summary>
        TooFast,
        /// <summary>Slower than the minimum steering speed for too long.</summary>
        TooSlow,
        /// <summary>No messages from the steer module.</summary>
        NoModule,
        /// <summary>The steer switch was switched off, or the driver turned the wheel.</summary>
        SteerSwitch,
    }

    /// <summary>
    /// AutoSteer settings: what the tablet checks, plus what is sent to the module (PGN 252 and
    /// 251, same meaning and defaults as the Windows AgOpenGPS steer settings).
    /// </summary>
    public sealed class SteerSettings
    {
        // --- Tablet side ---
        /// <summary>Front axle to rear axle, metres. Used for the pure pursuit wheel angle.</summary>
        public double Wheelbase { get; set; } = 2.5;
        /// <summary>Largest wheel angle asked for, degrees.</summary>
        public double MaxSteerAngle { get; set; } = 35;
        /// <summary>Below this the module does not steer, and after <see cref="SteerController.SlowSeconds"/> AutoSteer switches off.</summary>
        public double MinSpeedKmh { get; set; } = 1.0;
        /// <summary>Above this AutoSteer switches off at once.</summary>
        public double MaxSpeedKmh { get; set; } = 15;
        /// <summary>Further than this from the line AutoSteer switches off (and will not switch on).</summary>
        public double MaxOffLineMeters { get; set; } = 2.0;
        /// <summary>Only steer with an RTK fix (a short drop to float is tolerated).</summary>
        public bool RequireRtkFix { get; set; } = true;
        /// <summary>The steer switch / button on the module switches AutoSteer on, too (not only off).</summary>
        public bool SwitchEngages { get; set; }

        // --- PGN 252 ---
        /// <summary>PWM per degree of wheel angle error.</summary>
        public int ProportionalGain { get; set; } = 50;
        /// <summary>Largest motor PWM (0..255). The low PWM is a third of it.</summary>
        public int HighPwm { get; set; } = 180;
        /// <summary>PWM added so the motor moves at all (overcomes friction).</summary>
        public int MinPwm { get; set; } = 25;
        /// <summary>Wheel angle sensor counts per degree.</summary>
        public int CountsPerDegree { get; set; } = 100;
        /// <summary>Wheel angle sensor zero, in counts (wheels straight = 0 degrees).</summary>
        public int WasOffset { get; set; }
        /// <summary>Left turn correction for the sensor, percent (100 = none).</summary>
        public int Ackermann { get; set; } = 100;

        // --- PGN 251 ---
        public bool InvertWas { get; set; }
        public bool InvertMotor { get; set; }
        public bool RelayActiveHigh { get; set; }
        public bool SingleInputWas { get; set; } = true;
        /// <summary>Cytron style driver (PWM + direction) rather than IBT-2 (two PWM).</summary>
        public bool CytronDriver { get; set; } = true;
        public SteerSwitchType SwitchType { get; set; } = SteerSwitchType.None;
        public bool ShaftEncoder { get; set; }
        public bool CurrentSensor { get; set; }
        /// <summary>Encoder counts (or current) that mean the driver took the wheel.</summary>
        public int MaxPulseCounts { get; set; } = 3;

        public SteerSettings Clone() => (SteerSettings)MemberwiseClone();
    }

    /// <summary>What the controller needs to know each cycle.</summary>
    public sealed class SteerInputs
    {
        public DateTime Utc = DateTime.UtcNow;
        public bool LinkConnected;
        /// <summary>Seconds since the last fix; NaN without one.</summary>
        public double FixAgeSeconds = double.NaN;
        public int FixQuality;
        public double SpeedKmh = double.NaN;
        /// <summary>Distance off the line, metres, positive = right of it; NaN without a line.</summary>
        public double CrossTrackMeters = double.NaN;
        /// <summary>Pure pursuit goal point angle (from <see cref="SteerResult"/>); NaN without a line or heading.</summary>
        public double SteerDegrees = double.NaN;
        public double LookaheadMeters = double.NaN;
        public bool Working;
        /// <summary>Latest message from the module, or null.</summary>
        public SteerFeedback Feedback;

        /// <summary>Fills the guidance part from a snapshot.</summary>
        public static SteerInputs From(GuidanceSnapshot s, bool linkConnected, SteerFeedback feedback, DateTime utc)
        {
            var i = new SteerInputs { Utc = utc, LinkConnected = linkConnected, Feedback = feedback, Working = s != null && s.Working };
            GnssFix fix = s?.Fix;
            if (fix != null && fix.HasPosition)
            {
                i.FixAgeSeconds = Math.Max(0, (utc - fix.ReceivedUtc).TotalSeconds);
                i.FixQuality = fix.FixQuality;
                i.SpeedKmh = fix.SpeedKmh;
            }
            if (s?.Result != null) i.CrossTrackMeters = s.Result.CrossTrackMeters;
            if (s?.Steer != null)
            {
                i.SteerDegrees = s.Steer.SteerDegrees;
                i.LookaheadMeters = s.Steer.LookaheadMeters;
            }
            return i;
        }
    }

    /// <summary>One cycle's output: what to send to the module.</summary>
    public sealed class SteerCommand
    {
        public bool On;
        /// <summary>Wanted wheel angle, degrees, positive = right.</summary>
        public double AngleDeg;
        /// <summary>The PGN 254 frame to send.</summary>
        public byte[] Frame;
    }

    /// <summary>
    /// AutoSteer on the tablet: decides whether steering may be on, and works out the wheel angle
    /// the module should hold (pure pursuit on the current pass, as AgOpenGPS does). The module only
    /// turns the wheel to that angle. Every check that fails switches AutoSteer off and says why,
    /// so the driver is never surprised by a tractor that steers on its own. Not thread safe: call
    /// it from one loop.
    /// </summary>
    public sealed class SteerController
    {
        /// <summary>Messages from the module older than this mean it is gone.</summary>
        public const double ModuleTimeoutSeconds = 1.0;
        /// <summary>A fix older than this means there is no position.</summary>
        public const double FixTimeoutSeconds = 1.0;
        /// <summary>RTK fix may drop (to float) for this long before steering stops.</summary>
        public const double RtkGraceSeconds = 3.0;
        /// <summary>Below the minimum speed for this long switches steering off (stopping at the headland).</summary>
        public const double SlowSeconds = 10.0;

        private bool _engaged;
        private bool _sawSwitchOn;
        private bool? _lastSwitchHigh;
        private DateTime? _rtkBadSince, _slowSince;
        private SteerOffReason _pendingOff = SteerOffReason.None;

        public SteerSettings Settings { get; set; } = new SteerSettings();

        public bool Engaged => _engaged;

        /// <summary>Why AutoSteer last went off (None while on or never on).</summary>
        public SteerOffReason LastOffReason { get; private set; }

        /// <summary>
        /// Switches AutoSteer on if everything is in order. Returns <see cref="SteerOffReason.None"/>
        /// when it is on, otherwise what is missing (AutoSteer stays off).
        /// </summary>
        public SteerOffReason TryEngage(SteerInputs i)
        {
            if (_engaged) return SteerOffReason.None;
            SteerOffReason r = Check(i, engaging: true);
            if (r != SteerOffReason.None) return r;
            _engaged = true;
            _sawSwitchOn = false;
            _rtkBadSince = _slowSince = null;
            LastOffReason = SteerOffReason.None;
            return SteerOffReason.None;
        }

        /// <summary>Switches AutoSteer off.</summary>
        public void Disengage(SteerOffReason reason = SteerOffReason.UserOff)
        {
            if (!_engaged) return;
            _engaged = false;
            LastOffReason = reason;
            if (reason != SteerOffReason.UserOff) _pendingOff = reason;
        }

        /// <summary>
        /// The reason AutoSteer switched itself off since the last call (for a sound and a message),
        /// or None. Switching off on the tablet is not reported.
        /// </summary>
        public SteerOffReason TakeAutomaticOff()
        {
            SteerOffReason r = _pendingOff;
            _pendingOff = SteerOffReason.None;
            return r;
        }

        /// <summary>One cycle (about 10 Hz): checks, and the command for the module.</summary>
        public SteerCommand Update(SteerInputs i)
        {
            SteerFeedback fb = i.Feedback != null && (i.Utc - i.Feedback.ReceivedUtc).TotalSeconds <= ModuleTimeoutSeconds ? i.Feedback : null;

            // The steer switch / button on the module.
            if (fb != null)
            {
                bool high = fb.SteerSwitchHigh;
                if (_engaged)
                {
                    if (!high) _sawSwitchOn = true;
                    else if (_sawSwitchOn) Disengage(SteerOffReason.SteerSwitch);
                }
                else if (Settings.SwitchEngages && Settings.SwitchType != SteerSwitchType.None
                    && _lastSwitchHigh == true && !high)
                {
                    SteerOffReason r = TryEngage(i);
                    if (r != SteerOffReason.None) _pendingOff = r;
                }
                _lastSwitchHigh = high;
            }

            if (_engaged)
            {
                SteerOffReason r = Check(i, engaging: false);
                if (r != SteerOffReason.None) Disengage(r);
            }

            double angle = 0;
            if (!double.IsNaN(i.SteerDegrees) && !double.IsNaN(i.LookaheadMeters))
            {
                angle = Steering.PurePursuitWheelAngle(i.SteerDegrees, i.LookaheadMeters, Settings.Wheelbase);
                double max = Math.Max(1, Settings.MaxSteerAngle);
                angle = Math.Max(-max, Math.Min(max, angle));
            }

            return new SteerCommand
            {
                On = _engaged,
                AngleDeg = _engaged ? angle : 0,
                Frame = SteerPgn.BuildAutoSteerData(i.SpeedKmh, _engaged, _engaged ? angle : 0,
                    _engaged ? i.CrossTrackMeters : double.NaN, (ushort)(i.Working ? 1 : 0)),
            };
        }

        private SteerOffReason Check(SteerInputs i, bool engaging)
        {
            SteerSettings s = Settings;
            if (!i.LinkConnected) return SteerOffReason.LinkLost;
            if (double.IsNaN(i.FixAgeSeconds) || i.FixAgeSeconds > FixTimeoutSeconds) return SteerOffReason.NoPosition;

            bool rtkOk = s.RequireRtkFix ? i.FixQuality == 4 : i.FixQuality > 0;
            if (!rtkOk)
            {
                if (engaging) return SteerOffReason.NoRtk;
                // A fix that is not good enough at all (no fix) stops at once; RTK float gets a grace time.
                if (i.FixQuality <= 0) return SteerOffReason.NoRtk;
                _rtkBadSince ??= i.Utc;
                if ((i.Utc - _rtkBadSince.Value).TotalSeconds > RtkGraceSeconds) return SteerOffReason.NoRtk;
            }
            else _rtkBadSince = null;

            if (double.IsNaN(i.SteerDegrees) || double.IsNaN(i.CrossTrackMeters)) return SteerOffReason.NoLine;
            if (Math.Abs(i.CrossTrackMeters) > s.MaxOffLineMeters) return SteerOffReason.OffLine;

            double speed = double.IsNaN(i.SpeedKmh) ? 0 : Math.Abs(i.SpeedKmh);
            if (speed > s.MaxSpeedKmh) return SteerOffReason.TooFast;
            if (!engaging)
            {
                if (speed < s.MinSpeedKmh)
                {
                    _slowSince ??= i.Utc;
                    if ((i.Utc - _slowSince.Value).TotalSeconds > SlowSeconds) return SteerOffReason.TooSlow;
                }
                else _slowSince = null;
            }

            SteerFeedback fb = i.Feedback;
            if (fb == null || (i.Utc - fb.ReceivedUtc).TotalSeconds > ModuleTimeoutSeconds) return SteerOffReason.NoModule;
            return SteerOffReason.None;
        }

        /// <summary>The message for a reason (English; translated where shown).</summary>
        public static string Message(SteerOffReason r)
        {
            switch (r)
            {
                case SteerOffReason.UserOff: return "AutoSteer switched off";
                case SteerOffReason.LinkLost: return "AutoSteer: no connection";
                case SteerOffReason.NoPosition: return "AutoSteer: no position";
                case SteerOffReason.NoRtk: return "AutoSteer: no RTK fix";
                case SteerOffReason.NoLine: return "AutoSteer: no guidance line";
                case SteerOffReason.OffLine: return "AutoSteer: too far from the line";
                case SteerOffReason.TooFast: return "AutoSteer: too fast";
                case SteerOffReason.TooSlow: return "AutoSteer: too slow";
                case SteerOffReason.NoModule: return "AutoSteer: no steer module";
                case SteerOffReason.SteerSwitch: return "AutoSteer: steer switch off or wheel turned";
                default: return "";
            }
        }
    }

    /// <summary>
    /// The steer module's motor control, the same as the AgOpenGPS steer firmware (calcSteeringPID):
    /// proportional, plus a minimum PWM to overcome friction, limited to the low PWM near the target
    /// and the high PWM further away. Used by the simulated module; the ESP32 firmware does the same.
    /// </summary>
    public static class SteerPid
    {
        /// <summary>Within this many degrees of error the PWM is limited between low and high.</summary>
        public const double LowHighDegrees = 3.0;

        /// <summary>
        /// Motor PWM, -255..255, with the firmware's sign: positive when the wheel is right of the
        /// setpoint (so the motor turns it left).
        /// </summary>
        public static int Pwm(double setpoint, double actual, SteerSettings s)
        {
            double error = actual - setpoint;
            double pwm = s.ProportionalGain * error;
            double errorAbs = Math.Abs(error);
            int high = s.HighPwm, low = s.HighPwm / 3;
            double highLowPerDeg = (high - low) / LowHighDegrees;
            double newMax = errorAbs < LowHighDegrees ? errorAbs * highLowPerDeg + low : high;

            int drive = (int)pwm;
            if (drive < 0) drive -= s.MinPwm;
            else if (drive > 0) drive += s.MinPwm;
            if (drive > newMax) drive = (int)newMax;
            else if (drive < -newMax) drive = -(int)newMax;
            if (s.InvertMotor) drive = -drive;
            return drive;
        }
    }
}
