using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class SteerPgnTests
    {
        [Test]
        public void AutoSteerData_MatchesAgOpenGpsLayout()
        {
            // 8.5 km/h, on, 2.5 degrees right, 10 cm right of the line, section 1 on.
            byte[] f = SteerPgn.BuildAutoSteerData(8.5, true, 2.5, 0.10, 1);
            Assert.That(f.Length, Is.EqualTo(14));
            Assert.That(f.Take(5), Is.EqualTo(new byte[] { 0x80, 0x81, 0x7F, 0xFE, 8 }));
            Assert.That(f[5] | (f[6] << 8), Is.EqualTo(85));       // km/h * 10
            Assert.That(f[7], Is.EqualTo(1));
            Assert.That((short)(f[8] | (f[9] << 8)), Is.EqualTo(250)); // degrees * 100
            Assert.That(f[10], Is.EqualTo(127 + 5));                // 100 mm * 0.05
            Assert.That(f[11], Is.EqualTo(1));
            Assert.That(Pgn.IsValid(f), Is.True);
        }

        [Test]
        public void AutoSteerData_NegativeAngleAndOffSendsNoLine()
        {
            byte[] f = SteerPgn.BuildAutoSteerData(5, false, -12.34, 0.5);
            Assert.That((short)(f[8] | (f[9] << 8)), Is.EqualTo(-1234));
            Assert.That(f[7], Is.EqualTo(0));
            Assert.That(f[10], Is.EqualTo(SteerPgn.NoLineDistance));
            Assert.That(SteerPgn.ParseAutoSteerData(f, out double speed, out bool on, out double angle), Is.True);
            Assert.That(speed, Is.EqualTo(5).Within(0.01));
            Assert.That(on, Is.False);
            Assert.That(angle, Is.EqualTo(-12.34).Within(0.001));
        }

        [Test]
        public void AutoSteerData_LineDistanceIsClamped()
        {
            Assert.That(SteerPgn.BuildAutoSteerData(5, true, 0, -9.0)[10], Is.EqualTo(0));
            Assert.That(SteerPgn.BuildAutoSteerData(5, true, 0, 9.0)[10], Is.EqualTo(254));
        }

        [Test]
        public void Feedback_RoundTrips_AndReadsSwitchBits()
        {
            byte[] f = SteerPgn.BuildFeedback(-3.21, true, true, 120);
            Assert.That(f[2], Is.EqualTo(SteerPgn.SteerSource));
            SteerFeedback fb = SteerPgn.ParseFeedback(f, DateTime.UtcNow);
            Assert.That(fb.ActualAngle, Is.EqualTo(-3.21).Within(0.001));
            Assert.That(fb.WorkSwitchHigh, Is.True);
            Assert.That(fb.SteerSwitchHigh, Is.True);
            Assert.That(fb.Pwm, Is.EqualTo(120));
            Assert.That(double.IsNaN(fb.ImuHeading), Is.True);
            Assert.That(double.IsNaN(fb.ImuRoll), Is.True);

            fb = SteerPgn.ParseFeedback(SteerPgn.BuildFeedback(1, false, false, 0, 123.4, -2.5), DateTime.UtcNow);
            Assert.That(fb.ImuHeading, Is.EqualTo(123.4).Within(0.01));
            Assert.That(fb.ImuRoll, Is.EqualTo(-2.5).Within(0.01));
            Assert.That(fb.SteerSwitchHigh, Is.False);
        }

        [Test]
        public void Feedback_RejectsBadCrcAndOtherPgns()
        {
            byte[] f = SteerPgn.BuildFeedback(1, false, false, 0);
            f[6] ^= 1;
            Assert.That(SteerPgn.ParseFeedback(f, DateTime.UtcNow), Is.Null);
            Assert.That(SteerPgn.ParseFeedback(SteerPgn.BuildAutoSteerData(1, true, 0, 0), DateTime.UtcNow), Is.Null);
        }

        [Test]
        public void Settings_MatchLayout_AndRoundTrip()
        {
            var s = new SteerSettings { ProportionalGain = 60, HighPwm = 200, MinPwm = 30, CountsPerDegree = 110, WasOffset = -345, Ackermann = 95 };
            byte[] f = SteerPgn.BuildSettings(s);
            Assert.That(f[3], Is.EqualTo(0xFC));
            Assert.That(f[5], Is.EqualTo(60));
            Assert.That(f[6], Is.EqualTo(200));
            Assert.That(f[7], Is.EqualTo(66)); // a third of high
            Assert.That(f[8], Is.EqualTo(30));
            Assert.That(f[9], Is.EqualTo(110));
            Assert.That((short)(f[10] | (f[11] << 8)), Is.EqualTo(-345));
            Assert.That(f[12], Is.EqualTo(95));

            var back = new SteerSettings();
            Assert.That(SteerPgn.ParseSettings(f, back), Is.True);
            Assert.That(back.WasOffset, Is.EqualTo(-345));
            Assert.That(back.HighPwm, Is.EqualTo(200));
        }

        [Test]
        public void Config_Bits()
        {
            var s = new SteerSettings
            {
                InvertWas = true, InvertMotor = true, SingleInputWas = false, CytronDriver = true,
                SwitchType = SteerSwitchType.Button, MinSpeedKmh = 1.5, MaxPulseCounts = 7,
            };
            byte[] f = SteerPgn.BuildConfig(s);
            Assert.That(f[3], Is.EqualTo(0xFB));
            Assert.That(f[5], Is.EqualTo(0b0101_0101));
            Assert.That(f[6], Is.EqualTo(7));
            Assert.That(f[7], Is.EqualTo(15));
            Assert.That(Pgn.IsValid(f), Is.True);
        }
    }

    public class SteerControllerTests
    {
        private DateTime _t;
        private SteerController _c;

        [SetUp]
        public void SetUp()
        {
            _t = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
            _c = new SteerController();
        }

        private SteerInputs Good(Action<SteerInputs> change = null)
        {
            var i = new SteerInputs
            {
                Utc = _t, LinkConnected = true, FixAgeSeconds = 0.1, FixQuality = 4, SpeedKmh = 8,
                CrossTrackMeters = 0.2, SteerDegrees = 3, LookaheadMeters = 4,
                Feedback = new SteerFeedback { ReceivedUtc = _t, SteerSwitchHigh = false },
            };
            change?.Invoke(i);
            return i;
        }

        private SteerCommand Tick(double seconds, Action<SteerInputs> change = null)
        {
            _t = _t.AddSeconds(seconds);
            return _c.Update(Good(change));
        }

        [Test]
        public void Engages_WhenAllIsWell_AndSendsPurePursuitAngle()
        {
            Assert.That(_c.TryEngage(Good()), Is.EqualTo(SteerOffReason.None));
            SteerCommand cmd = Tick(0.1);
            Assert.That(cmd.On, Is.True);
            double expected = Steering.PurePursuitWheelAngle(3, 4, _c.Settings.Wheelbase);
            Assert.That(cmd.AngleDeg, Is.EqualTo(expected).Within(1e-9));
            Assert.That(cmd.Frame[7], Is.EqualTo(1));
            Assert.That((short)(cmd.Frame[8] | (cmd.Frame[9] << 8)), Is.EqualTo((int)(expected * 100)));
        }

        [Test]
        public void AngleIsLimitedToMaxSteerAngle()
        {
            _c.Settings.MaxSteerAngle = 10;
            _c.TryEngage(Good());
            Assert.That(Tick(0.1, i => i.SteerDegrees = 40).AngleDeg, Is.EqualTo(10));
            Assert.That(Tick(0.1, i => i.SteerDegrees = -40).AngleDeg, Is.EqualTo(-10));
        }

        [TestCase(SteerOffReason.LinkLost)]
        [TestCase(SteerOffReason.NoPosition)]
        [TestCase(SteerOffReason.NoRtk)]
        [TestCase(SteerOffReason.NoLine)]
        [TestCase(SteerOffReason.OffLine)]
        [TestCase(SteerOffReason.TooFast)]
        [TestCase(SteerOffReason.NoModule)]
        public void WillNotEngage_AndSaysWhy(SteerOffReason reason)
        {
            SteerInputs i = Good(Breaker(reason));
            Assert.That(_c.TryEngage(i), Is.EqualTo(reason));
            Assert.That(_c.Engaged, Is.False);
            Assert.That(_c.Update(i).Frame[7], Is.EqualTo(0));
        }

        [TestCase(SteerOffReason.LinkLost)]
        [TestCase(SteerOffReason.NoPosition)]
        [TestCase(SteerOffReason.NoLine)]
        [TestCase(SteerOffReason.OffLine)]
        [TestCase(SteerOffReason.TooFast)]
        [TestCase(SteerOffReason.NoModule)]
        public void SwitchesOffAtOnce_AndReportsIt(SteerOffReason reason)
        {
            _c.TryEngage(Good());
            Tick(0.1);
            SteerCommand cmd = Tick(0.1, Breaker(reason));
            Assert.That(cmd.On, Is.False);
            Assert.That(cmd.Frame[7], Is.EqualTo(0));
            Assert.That(cmd.Frame[10], Is.EqualTo(SteerPgn.NoLineDistance));
            Assert.That(_c.LastOffReason, Is.EqualTo(reason));
            Assert.That(_c.TakeAutomaticOff(), Is.EqualTo(reason));
            Assert.That(_c.TakeAutomaticOff(), Is.EqualTo(SteerOffReason.None));
        }

        private Action<SteerInputs> Breaker(SteerOffReason reason)
        {
            switch (reason)
            {
                case SteerOffReason.LinkLost: return i => i.LinkConnected = false;
                case SteerOffReason.NoPosition: return i => i.FixAgeSeconds = 2;
                case SteerOffReason.NoRtk: return i => i.FixQuality = 1;
                case SteerOffReason.NoLine: return i => { i.SteerDegrees = double.NaN; i.CrossTrackMeters = double.NaN; };
                case SteerOffReason.OffLine: return i => i.CrossTrackMeters = -2.5;
                case SteerOffReason.TooFast: return i => i.SpeedKmh = 20;
                case SteerOffReason.NoModule: return i => i.Feedback.ReceivedUtc = _t.AddSeconds(-1.5);
                default: throw new ArgumentException();
            }
        }

        [Test]
        public void RtkFloat_HasAGraceTime()
        {
            _c.TryEngage(Good());
            for (int k = 0; k < 25; k++) Assert.That(Tick(0.1, i => i.FixQuality = 5).On, Is.True);
            for (int k = 0; k < 10; k++) Tick(0.1, i => i.FixQuality = 5);
            Assert.That(_c.Engaged, Is.False);
            Assert.That(_c.LastOffReason, Is.EqualTo(SteerOffReason.NoRtk));
        }

        [Test]
        public void RtkFloat_BackToFix_ResetsGrace()
        {
            _c.TryEngage(Good());
            for (int k = 0; k < 25; k++) Tick(0.1, i => i.FixQuality = 5);
            Tick(0.1);
            for (int k = 0; k < 25; k++) Tick(0.1, i => i.FixQuality = 5);
            Assert.That(_c.Engaged, Is.True);
        }

        [Test]
        public void WithoutRtkRequirement_GpsIsEnough()
        {
            _c.Settings.RequireRtkFix = false;
            Assert.That(_c.TryEngage(Good(i => i.FixQuality = 1)), Is.EqualTo(SteerOffReason.None));
        }

        [Test]
        public void Slow_SwitchesOffOnlyAfterAWhile()
        {
            Assert.That(_c.TryEngage(Good(i => i.SpeedKmh = 0)), Is.EqualTo(SteerOffReason.None));
            for (int k = 0; k < 95; k++) Tick(0.1, i => i.SpeedKmh = 0.3);
            Assert.That(_c.Engaged, Is.True);
            for (int k = 0; k < 10; k++) Tick(0.1, i => i.SpeedKmh = 0.3);
            Assert.That(_c.LastOffReason, Is.EqualTo(SteerOffReason.TooSlow));
        }

        [Test]
        public void SteerSwitchOff_AfterItWasOn_SwitchesOff()
        {
            _c.TryEngage(Good());
            // The module may still report "off" until it saw the first command: not a reason to stop.
            Tick(0.1, i => i.Feedback.SteerSwitchHigh = true);
            Assert.That(_c.Engaged, Is.True);
            Tick(0.1);
            Tick(0.1, i => i.Feedback.SteerSwitchHigh = true);
            Assert.That(_c.Engaged, Is.False);
            Assert.That(_c.TakeAutomaticOff(), Is.EqualTo(SteerOffReason.SteerSwitch));
        }

        [Test]
        public void SteerSwitch_CanEngage_WhenSetUp()
        {
            _c.Settings.SwitchType = SteerSwitchType.Switch;
            Tick(0.1, i => i.Feedback.SteerSwitchHigh = true);
            Tick(0.1);
            Assert.That(_c.Engaged, Is.False, "not set to engage from the switch");

            _c.Settings.SwitchEngages = true;
            Tick(0.1, i => i.Feedback.SteerSwitchHigh = true);
            Tick(0.1);
            Assert.That(_c.Engaged, Is.True);
        }

        [Test]
        public void UserOff_IsNotReportedAsAutomatic()
        {
            _c.TryEngage(Good());
            _c.Disengage();
            Assert.That(_c.LastOffReason, Is.EqualTo(SteerOffReason.UserOff));
            Assert.That(_c.TakeAutomaticOff(), Is.EqualTo(SteerOffReason.None));
        }
    }

    public class SteerPidTests
    {
        [Test]
        public void Pwm_FollowsFirmware()
        {
            var s = new SteerSettings { ProportionalGain = 50, HighPwm = 180, MinPwm = 25 };
            Assert.That(SteerPid.Pwm(0, 0, s), Is.EqualTo(0));
            // 1 degree right of the setpoint: 50 + 25 = 75, limit near target 1 * 40 + 60 = 100.
            Assert.That(SteerPid.Pwm(0, 1, s), Is.EqualTo(75));
            Assert.That(SteerPid.Pwm(1, 0, s), Is.EqualTo(-75));
            // 0.1 degree: 5 + 25 = 30, limit 0.1 * 40 + 60 = 64.
            Assert.That(SteerPid.Pwm(0, 0.1, s), Is.EqualTo(30));
            // Far: limited to high.
            Assert.That(SteerPid.Pwm(0, 20, s), Is.EqualTo(180));
            s.InvertMotor = true;
            Assert.That(SteerPid.Pwm(0, 20, s), Is.EqualTo(-180));
        }
    }

    public class RtcmFramingTests
    {
        private static byte[] Rtcm(int payload, byte fill = 0x80)
        {
            var f = new byte[3 + payload + 3];
            f[0] = 0xD3;
            f[1] = (byte)((payload >> 8) & 0x03);
            f[2] = (byte)payload;
            for (int i = 3; i < 3 + payload; i++) f[i] = fill; // 0x80 0x81 inside must not look like PGN
            if (payload > 1) f[4] = 0x81;
            return f;
        }

        [Test]
        public void Tracker_FindsFrameEnds()
        {
            var t = new RtcmFrameTracker();
            byte[] f = Rtcm(300);
            for (int i = 0; i < f.Length - 1; i++) Assert.That(t.Feed(f[i]), Is.False, "byte " + i);
            Assert.That(t.Feed(f[f.Length - 1]), Is.True);
            Assert.That(t.Feed(0x55), Is.True, "loose bytes are complete on their own");
        }

        [Test]
        public void SteerFrames_GoBetweenRtcmFrames_NotInside()
        {
            var output = new MemoryStream();
            var session = new GnssSession(new MemoryStream(), output);
            byte[] a = Rtcm(400), b = Rtcm(100);
            byte[] steer = SteerPgn.BuildAutoSteerData(8, true, 1.5, 0.05);

            // First 256 bytes of frame a, then a steer frame arrives, then the rest in two chunks.
            session.WriteCorrections(a, 0, 256);
            session.WriteFrame(steer);
            session.WriteFrame(SteerPgn.BuildAutoSteerData(8, true, 2.0, 0.05)); // replaces the waiting one
            var rest = a.Skip(256).Concat(b).ToArray();
            session.WriteCorrections(rest, 0, 200);
            session.WriteCorrections(rest, 200, rest.Length - 200);

            byte[] sent = output.ToArray();
            byte[] newest = SteerPgn.BuildAutoSteerData(8, true, 2.0, 0.05);
            byte[] expected = a.Concat(newest).Concat(b).ToArray();
            Assert.That(sent, Is.EqualTo(expected));
        }

        [Test]
        public void SteerFrame_IsSentAtOnce_BetweenFrames()
        {
            var output = new MemoryStream();
            var session = new GnssSession(new MemoryStream(), output);
            byte[] a = Rtcm(50);
            session.WriteCorrections(a, 0, a.Length);
            byte[] steer = SteerPgn.BuildAutoSteerData(8, false, 0, double.NaN);
            Assert.That(session.WriteFrame(steer), Is.True);
            Assert.That(output.ToArray(), Is.EqualTo(a.Concat(steer).ToArray()));
            Assert.That(session.GetStats().PgnFramesOut, Is.EqualTo(1));
        }

        [Test]
        public void Session_RaisesSteerFeedback()
        {
            byte[] fb = SteerPgn.BuildFeedback(4.5, false, false, 80);
            byte[] gga = System.Text.Encoding.ASCII.GetBytes("$GNGGA,1*00\r\n");
            var input = new MemoryStream(gga.Concat(fb).ToArray());
            var session = new GnssSession(input, null);
            var got = new List<SteerFeedback>();
            session.SteerFeedbackReceived += got.Add;
            session.RunAsync(default).Wait(TimeSpan.FromSeconds(5));
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].ActualAngle, Is.EqualTo(4.5).Within(0.001));
        }
    }

    public class SimSteerModuleTests
    {
        [Test]
        public void SilentUntilTheAppTalksToIt()
        {
            var m = new SimSteerModule();
            Assert.That(m.Feedback(0), Is.Null);
            m.Receive(SteerPgn.BuildAutoSteerData(5, false, 0, double.NaN), DateTime.UtcNow);
            Assert.That(m.Feedback(0), Is.Not.Null);
        }

        [Test]
        public void TurnsTheWheelToTheSetpoint()
        {
            var m = new SimSteerModule();
            DateTime now = DateTime.UtcNow;
            m.Receive(SteerPgn.BuildAutoSteerData(8, true, 10, 0.1), now);
            Assert.That(m.IsSteering(now), Is.True);
            double wheel = 0;
            for (int k = 0; k < 20; k++) wheel = m.Step(wheel, 0.1, now);
            Assert.That(wheel, Is.EqualTo(10).Within(0.3));
        }

        [Test]
        public void Watchdog_StopsSteering()
        {
            var m = new SimSteerModule();
            DateTime now = DateTime.UtcNow;
            m.Receive(SteerPgn.BuildAutoSteerData(8, true, 10, 0.1), now);
            Assert.That(m.IsSteering(now.AddSeconds(1.5)), Is.False);
        }

        [Test]
        public void DriverOverride_ReportsSteerSwitchOff_UntilAppSwitchesOff()
        {
            var sim = new GnssSimulator(45.5, 16.3) { SpeedKmh = 8 };
            sim.SteerModule.Receive(SteerPgn.BuildAutoSteerData(8, true, 5, 0.1), DateTime.UtcNow);
            sim.DriverTurnsWheel(-10);
            Assert.That(sim.SteerModule.IsSteering(DateTime.UtcNow), Is.False);
            Assert.That(SteerPgn.ParseFeedback(sim.SteerModule.Feedback(sim.WheelAngle), DateTime.UtcNow).SteerSwitchHigh, Is.True);
            sim.SteerModule.Receive(SteerPgn.BuildAutoSteerData(8, false, 0, double.NaN), DateTime.UtcNow);
            Assert.That(SteerPgn.ParseFeedback(sim.SteerModule.Feedback(sim.WheelAngle), DateTime.UtcNow).SteerSwitchHigh, Is.False);
        }

        [Test]
        public void ClosedLoop_ControllerAndModule_PullTheTractorOntoTheLine()
        {
            // A line due north through the origin; the tractor starts 1 m to its right, heading north.
            var plane = new LocalPlane(45.5, 16.3);
            plane.ToWgs84(new GeoPoint(0, 1.0), out double lat, out double lon);
            var sim = new GnssSimulator(lat, lon, 0) { SpeedKmh = 8, Wheelbase = 2.5 };
            var pass = new List<GeoPoint> { new GeoPoint(-1000, 0), new GeoPoint(1000, 0) };
            var c = new SteerController();
            c.Settings.Wheelbase = 2.5;

            DateTime t = DateTime.UtcNow;
            SteerFeedback fb = null;
            double maxAfter = 0;
            for (int k = 0; k < 400; k++) // 40 s at 10 Hz
            {
                GeoPoint v = plane.ToLocal(sim.Latitude, sim.Longitude);
                SteerResult steer = Steering.Compute(pass, v, sim.Heading, true, Steering.LookaheadMeters(sim.SpeedKmh));
                var i = new SteerInputs
                {
                    Utc = DateTime.UtcNow, LinkConnected = true, FixAgeSeconds = 0.05, FixQuality = 4, SpeedKmh = sim.SpeedKmh,
                    CrossTrackMeters = v.Easting, SteerDegrees = steer.SteerDegrees, LookaheadMeters = steer.LookaheadMeters, Feedback = fb,
                };
                if (k == 1) Assert.That(c.TryEngage(i), Is.EqualTo(SteerOffReason.None));
                SteerCommand cmd = c.Update(i);
                sim.SteerModule.Receive(cmd.Frame, DateTime.UtcNow);
                sim.Step(0.1);
                fb = SteerPgn.ParseFeedback(sim.SteerModule.Feedback(sim.WheelAngle), DateTime.UtcNow);
                if (k > 250) maxAfter = Math.Max(maxAfter, Math.Abs(v.Easting));
            }
            Assert.That(c.Engaged, Is.True, c.LastOffReason.ToString());
            Assert.That(maxAfter, Is.LessThan(0.05), "within 5 cm after 25 s");
        }
    }
}
