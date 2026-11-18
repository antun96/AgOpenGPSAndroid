using System;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>What the steer module reports back (PGN 253, 0xFD).</summary>
    public sealed class SteerFeedback
    {
        /// <summary>Measured wheel angle from the wheel angle sensor, degrees, positive = right.</summary>
        public double ActualAngle;
        /// <summary>IMU heading in degrees, NaN when the module has none.</summary>
        public double ImuHeading = double.NaN;
        /// <summary>IMU roll in degrees, NaN when the module has none.</summary>
        public double ImuRoll = double.NaN;
        /// <summary>Work switch input (bit 0).</summary>
        public bool WorkSwitchHigh;
        /// <summary>
        /// Steer switch input (bit 1). As in AgOpenGPS, high means the switch is OFF (released, or the
        /// module saw the driver turn the wheel): steering must stop.
        /// </summary>
        public bool SteerSwitchHigh;
        /// <summary>Motor PWM the module drives now, 0..255.</summary>
        public int Pwm;
        public DateTime ReceivedUtc;
    }

    /// <summary>
    /// The AgOpenGPS steer module messages, byte for byte as the Windows AgOpenGPS sends and reads
    /// them (all are 14-byte frames with 8 data bytes), so standard AgOpenGPS steer boards work too.
    /// <list type="bullet">
    /// <item>254 (0xFE) to the module, about 10 times a second: speed, on/off, wanted wheel angle, distance off the line, sections.</item>
    /// <item>253 (0xFD) from the module: actual wheel angle, IMU heading and roll, switches, PWM.</item>
    /// <item>252 (0xFC) to the module: steer settings (gain, PWM limits, sensor counts, zero, Ackermann).</item>
    /// <item>251 (0xFB) to the module: board configuration (inversions, driver type, switch type).</item>
    /// </list>
    /// </summary>
    public static class SteerPgn
    {
        public const byte AutoSteerData = 0xFE;
        public const byte Feedback = 0xFD;
        public const byte Settings = 0xFC;
        public const byte Config = 0xFB;
        /// <summary>Source byte AgOpenGPS puts in the messages it sends.</summary>
        public const byte AppSource = 0x7F;
        /// <summary>Source byte of the steer module.</summary>
        public const byte SteerSource = 0x7E;
        public const int FrameLength = 14;

        /// <summary>Line distance byte meaning "no line / steering off" (AgOpenGPS 32020 mm).</summary>
        public const byte NoLineDistance = 255;

        private static byte[] NewFrame(byte source, byte pgn)
        {
            var f = new byte[FrameLength];
            f[0] = Pgn.Header1; f[1] = Pgn.Header2; f[2] = source; f[3] = pgn; f[4] = 8;
            return f;
        }

        private static byte[] Done(byte[] f)
        {
            f[FrameLength - 1] = Pgn.Crc(f, FrameLength - 1);
            return f;
        }

        private static void PutInt16(byte[] f, int at, int value)
        {
            short v = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
            f[at] = unchecked((byte)v);
            f[at + 1] = unchecked((byte)(v >> 8));
        }

        private static short GetInt16(byte[] f, int at) => (short)(f[at] | (f[at + 1] << 8));

        /// <summary>
        /// PGN 254. <paramref name="crossTrackMeters"/> is the distance off the line (positive = right
        /// of it); NaN or steering off sends "no line". Sections 1-16 are bits of <paramref name="sections"/>.
        /// </summary>
        public static byte[] BuildAutoSteerData(double speedKmh, bool steerOn, double steerAngleDeg,
            double crossTrackMeters, ushort sections = 0)
        {
            byte[] f = NewFrame(AppSource, AutoSteerData);
            int speed = double.IsNaN(speedKmh) ? 0 : (int)(Math.Abs(speedKmh) * 10.0);
            speed = Math.Min(ushort.MaxValue, speed);
            f[5] = unchecked((byte)speed);
            f[6] = unchecked((byte)(speed >> 8));
            f[7] = (byte)(steerOn ? 1 : 0);
            PutInt16(f, 8, double.IsNaN(steerAngleDeg) ? 0 : (int)(steerAngleDeg * 100));

            // As AgOpenGPS: mm * 0.05 = units of 2 cm, limited to +-127, shifted by 127; 255 = no line.
            int distanceX2;
            if (!steerOn || double.IsNaN(crossTrackMeters)) distanceX2 = NoLineDistance;
            else
            {
                distanceX2 = (int)(Math.Round(crossTrackMeters * 1000.0, MidpointRounding.AwayFromZero) * 0.05);
                distanceX2 = Math.Max(-127, Math.Min(127, distanceX2)) + 127;
            }
            f[10] = (byte)distanceX2;
            f[11] = unchecked((byte)sections);
            f[12] = unchecked((byte)(sections >> 8));
            return Done(f);
        }

        /// <summary>Reads a PGN 254 (used by the simulated module and tests). False if it is not one.</summary>
        public static bool ParseAutoSteerData(byte[] f, out double speedKmh, out bool steerOn, out double steerAngleDeg)
        {
            speedKmh = 0; steerOn = false; steerAngleDeg = 0;
            if (!Pgn.IsValid(f) || f[3] != AutoSteerData || f.Length < FrameLength) return false;
            speedKmh = (f[5] | (f[6] << 8)) * 0.1;
            steerOn = f[7] == 1;
            steerAngleDeg = GetInt16(f, 8) * 0.01;
            return true;
        }

        /// <summary>PGN 252 from the steer settings.</summary>
        public static byte[] BuildSettings(SteerSettings s)
        {
            byte[] f = NewFrame(AppSource, Settings);
            f[5] = ClampByte(s.ProportionalGain);
            f[6] = ClampByte(s.HighPwm);
            f[7] = ClampByte(s.HighPwm / 3);   // low PWM: AgOpenGPS always sends a third of the high one
            f[8] = ClampByte(s.MinPwm);
            f[9] = ClampByte(s.CountsPerDegree);
            PutInt16(f, 10, s.WasOffset);
            f[12] = ClampByte(s.Ackermann);
            return Done(f);
        }

        /// <summary>Reads a PGN 252 into <paramref name="s"/> (simulated module, tests). False if it is not one.</summary>
        public static bool ParseSettings(byte[] f, SteerSettings s)
        {
            if (!Pgn.IsValid(f) || f[3] != Settings || f.Length < FrameLength) return false;
            s.ProportionalGain = f[5];
            s.HighPwm = f[6];
            s.MinPwm = f[8];
            s.CountsPerDegree = f[9];
            s.WasOffset = GetInt16(f, 10);
            s.Ackermann = f[12];
            return true;
        }

        /// <summary>PGN 251 from the board configuration.</summary>
        public static byte[] BuildConfig(SteerSettings s)
        {
            byte[] f = NewFrame(AppSource, Config);
            int set0 = 0;
            if (s.InvertWas) set0 |= 1 << 0;
            if (s.RelayActiveHigh) set0 |= 1 << 1;
            if (s.InvertMotor) set0 |= 1 << 2;
            if (s.SingleInputWas) set0 |= 1 << 3;
            if (s.CytronDriver) set0 |= 1 << 4;
            if (s.SwitchType == SteerSwitchType.Switch) set0 |= 1 << 5;
            if (s.SwitchType == SteerSwitchType.Button) set0 |= 1 << 6;
            if (s.ShaftEncoder) set0 |= 1 << 7;
            f[5] = (byte)set0;
            f[6] = ClampByte(s.MaxPulseCounts);
            f[7] = ClampByte((int)Math.Round(s.MinSpeedKmh * 10));
            int set1 = 0;
            if (s.CurrentSensor) set1 |= 1 << 2;
            f[8] = (byte)set1;
            f[9] = 0; // angular velocity steering: not used
            return Done(f);
        }

        /// <summary>PGN 253 from the module; null if the frame is not one.</summary>
        public static SteerFeedback ParseFeedback(byte[] f, DateTime receivedUtc)
        {
            if (!Pgn.IsValid(f) || f[3] != Feedback || f.Length != FrameLength) return null;
            var fb = new SteerFeedback
            {
                ActualAngle = GetInt16(f, 5) * 0.01,
                WorkSwitchHigh = (f[11] & 1) == 1,
                SteerSwitchHigh = (f[11] & 2) == 2,
                Pwm = f[12],
                ReceivedUtc = receivedUtc,
            };
            short heading = GetInt16(f, 7);
            if (heading != 9999) fb.ImuHeading = heading * 0.1;
            short roll = GetInt16(f, 9);
            if (roll != 8888) fb.ImuRoll = roll * 0.1;
            return fb;
        }

        /// <summary>Builds a PGN 253 (simulated module, tests). NaN heading/roll = no IMU.</summary>
        public static byte[] BuildFeedback(double actualAngle, bool workSwitchHigh, bool steerSwitchHigh, int pwm,
            double imuHeading = double.NaN, double imuRoll = double.NaN)
        {
            byte[] f = NewFrame(SteerSource, Feedback);
            PutInt16(f, 5, (int)Math.Round(actualAngle * 100));
            PutInt16(f, 7, double.IsNaN(imuHeading) ? 9999 : (int)Math.Round(imuHeading * 10));
            PutInt16(f, 9, double.IsNaN(imuRoll) ? 8888 : (int)Math.Round(imuRoll * 10));
            f[11] = (byte)((workSwitchHigh ? 1 : 0) | (steerSwitchHigh ? 2 : 0));
            f[12] = ClampByte(Math.Abs(pwm));
            return Done(f);
        }

        private static byte ClampByte(double v) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));
    }
}
