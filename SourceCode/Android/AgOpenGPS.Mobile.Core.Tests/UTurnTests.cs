using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class UTurnShapeTests
    {
        private static double Heading(GeoPoint a, GeoPoint b) =>
            LocalPlane.Wrap360(LocalPlane.ToDeg(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));

        [TestCase(12.0, 5.0)] // flat turn
        [TestCase(6.0, 5.0)]  // bulb turn
        [TestCase(3.0, 6.0)]  // tight bulb
        public void EndsOnTheNextPass_DrivingBack(double spacing, double radius)
        {
            var start = new GeoPoint(100, 20);
            List<GeoPoint> right = UTurn.Shape(start, 0, true, spacing, radius);
            GeoPoint end = right[right.Count - 1];
            Assert.That(end.Northing, Is.EqualTo(100).Within(1e-6));
            Assert.That(end.Easting, Is.EqualTo(20 + spacing).Within(1e-6));
            Assert.That(LocalPlane.AngleDiff(Heading(right[right.Count - 2], end), 180), Is.EqualTo(0).Within(3));
            Assert.That(LocalPlane.AngleDiff(Heading(right[0], right[1]), 0), Is.EqualTo(0).Within(3), "starts along the pass");

            // Never tighter than the radius: the heading changes by at most step / radius.
            for (int i = 1; i < right.Count - 1; i++)
            {
                double step = right[i - 1].DistanceTo(right[i]);
                if (step < 0.05 || right[i].DistanceTo(right[i + 1]) < 0.05) continue;
                double turn = Math.Abs(LocalPlane.AngleDiff(Heading(right[i], right[i + 1]), Heading(right[i - 1], right[i])));
                Assert.That(turn, Is.LessThanOrEqualTo(LocalPlane.ToDeg(step / radius) * 1.2 + 0.5), "point " + i);
            }

            double depth = right.Max(p => p.Northing) - 100;
            Assert.That(depth, Is.EqualTo(UTurn.Depth(spacing, radius)).Within(0.05));

            List<GeoPoint> left = UTurn.Shape(start, 0, false, spacing, radius);
            Assert.That(left[left.Count - 1].Easting, Is.EqualTo(20 - spacing).Within(1e-6));
        }

        [Test]
        public void Shape_IsRotatedWithTheHeading()
        {
            // Driving east, a right turn goes to the south.
            List<GeoPoint> t = UTurn.Shape(new GeoPoint(0, 0), 90, true, 10, 4);
            GeoPoint end = t[t.Count - 1];
            Assert.That(end.Northing, Is.EqualTo(-10).Within(1e-6));
            Assert.That(end.Easting, Is.EqualTo(0).Within(1e-6));
            Assert.That(t.Max(p => p.Easting), Is.EqualTo(4).Within(0.05));
        }

        [Test]
        public void Plan_StartsWhereThePassLeavesTheMainPart()
        {
            var headland = new List<GeoPoint> { new GeoPoint(15, 15), new GeoPoint(15, 105), new GeoPoint(185, 105), new GeoPoint(185, 15) };
            var boundary = new List<GeoPoint> { new GeoPoint(0, 0), new GeoPoint(0, 120), new GeoPoint(200, 120), new GeoPoint(200, 0) };
            var pass = new List<GeoPoint> { new GeoPoint(-50, 30), new GeoPoint(250, 30) };
            var next = new List<GeoPoint> { new GeoPoint(250, 36), new GeoPoint(-50, 36) };

            UTurnPlan p = UTurn.Plan(pass, next, headland, boundary, new GeoPoint(170, 30), true, 6, 5);
            Assert.That(p, Is.Not.Null);
            Assert.That(p.Start.Northing, Is.EqualTo(185).Within(1e-6), "not where it enters at the south end");
            Assert.That(p.DistanceToStart, Is.EqualTo(15).Within(1e-6));
            Assert.That(p.End.Easting, Is.EqualTo(36).Within(1e-6));
            Assert.That(p.Fits, Is.True);
            GeoPoint last = p.Path[p.Path.Count - 1];
            Assert.That(last.Easting, Is.EqualTo(36).Within(1e-6));
            Assert.That(last.Northing, Is.EqualTo(185 - UTurn.LeadMeters).Within(0.01), "lead-out back into the field");

            // A boundary too close: the turn does not fit.
            var tight = new List<GeoPoint> { new GeoPoint(0, 0), new GeoPoint(0, 120), new GeoPoint(190, 120), new GeoPoint(190, 0) };
            Assert.That(UTurn.Plan(pass, next, headland, tight, new GeoPoint(170, 30), true, 6, 5).Fits, Is.False);

            // Past the headland line there is nothing ahead.
            Assert.That(UTurn.Plan(pass, next, headland, boundary, new GeoPoint(195, 30), true, 6, 5), Is.Null);
        }
    }

    public class UTurnDrivingTests
    {
        private static readonly LocalPlane Plane = new LocalPlane(45.8150, 15.9819);

        private static GnssFix Fix(GnssSimulator sim) => new GnssFix
        {
            Latitude = sim.Latitude, Longitude = sim.Longitude, FixQuality = 4, SpeedKmh = sim.SpeedKmh,
            HeadingTrue = sim.Heading, ReceivedUtc = DateTime.UtcNow, Satellites = 14, Hdop = 0.7,
        };

        private static Field Rectangle(double north, double east)
        {
            var f = new Field { Name = "rect" };
            foreach (var (n, e) in new[] { (0.0, 0.0), (0.0, east), (north, east), (north, 0.0) })
            {
                Plane.ToWgs84(new GeoPoint(n, e), out double lat, out double lon);
                f.Boundary.Add(new TrackPoint(lat, lon));
            }
            return f;
        }

        /// <summary>A north-south line at <paramref name="east"/>, recorded by driving it.</summary>
        private static AbLine Line(GuidanceEngine g, double east)
        {
            g.OnFix(At(20, east, 0));
            g.SetA();
            g.OnFix(At(60, east, 0));
            AbLine line = g.SetB("AB", out string error);
            Assert.That(line, Is.Not.Null, error);
            return line;
        }

        private static GnssFix At(double n, double e, double heading)
        {
            Plane.ToWgs84(new GeoPoint(n, e), out double lat, out double lon);
            return new GnssFix { Latitude = lat, Longitude = lon, FixQuality = 4, SpeedKmh = 8, HeadingTrue = heading, ReceivedUtc = DateTime.UtcNow };
        }

        [TestCase(5.0, 0, 6.0)]   // into the next pass: bulb turn
        [TestCase(5.0, 1, 12.0)]  // into the 2nd pass: flat turn
        [TestCase(5.0, 2, 18.0)]  // into the 3rd pass
        public void AutoSteer_TurnsAtTheHeadland_IntoTheNextPass(double radius, int skip, double expectedShift)
        {
            var g = new GuidanceEngine { PassWidth = 6, ToolWidth = 6, HeadlandWidth = 15 };
            g.SetActiveField(Rectangle(200, 120));
            g.SetActiveLine(Line(g, 33));
            g.UTurnRadius = radius;
            g.UTurnSkip = skip;
            g.UTurnRight = true;
            g.UTurnEnabled = true;

            Plane.ToWgs84(new GeoPoint(120, 33), out double lat, out double lon);
            var sim = new GnssSimulator(lat, lon, 0) { SpeedKmh = 8, Wheelbase = 2.5 };
            var c = new SteerController();
            c.Settings.Wheelbase = 2.5;
            SteerFeedback fb = null;
            bool sawTurn = false;
            double maxNorth = 0, maxOff = 0;

            for (int k = 0; k < 900; k++) // 90 s at 10 Hz
            {
                g.OnFix(Fix(sim));
                GuidanceSnapshot s = g.GetSnapshot();
                SteerInputs i = SteerInputs.From(s, true, fb, DateTime.UtcNow);
                if (k == 1) Assert.That(c.TryEngage(i), Is.EqualTo(SteerOffReason.None));
                SteerCommand cmd = c.Update(i);
                Assert.That(c.Engaged || k == 0, Is.True, "switched off: " + c.LastOffReason + " at step " + k);
                sawTurn |= s.UTurnActive;
                if (s.UTurnActive) maxOff = Math.Max(maxOff, s.UTurnOffPath);
                sim.SteerModule.Receive(cmd.Frame, DateTime.UtcNow);
                sim.Step(0.1);
                fb = SteerPgn.ParseFeedback(sim.SteerModule.Feedback(sim.WheelAngle), DateTime.UtcNow);
                GeoPoint v = Plane.ToLocal(sim.Latitude, sim.Longitude);
                maxNorth = Math.Max(maxNorth, v.Northing);
                if (sawTurn && !s.UTurnActive && v.Northing < 150) break;
            }

            GeoPoint end = Plane.ToLocal(sim.Latitude, sim.Longitude);
            Assert.That(sawTurn, Is.True);
            Assert.That(c.Engaged, Is.True, c.LastOffReason.ToString());
            Assert.That(maxNorth, Is.LessThan(199), "stayed inside the field");
            Assert.That(maxOff, Is.LessThan(1.0), "followed the turn path");
            Assert.That(LocalPlane.AngleDiff(sim.Heading, 180), Is.EqualTo(0).Within(5), "driving back");
            Assert.That(end.Easting, Is.EqualTo(33 + expectedShift).Within(0.3), "on the next pass");
            Assert.That(g.UTurnRight, Is.False, "the next turn goes the other way");
        }

        [Test]
        public void TurnThatDoesNotFit_SwitchesAutoSteerOff()
        {
            var g = new GuidanceEngine { PassWidth = 6, ToolWidth = 6, HeadlandWidth = 6 };
            g.SetActiveField(Rectangle(200, 120));
            g.SetActiveLine(Line(g, 33));
            g.UTurnRadius = 6; // bulb 6 m pass spacing needs about 11 m: more than the 6 m headland
            g.UTurnEnabled = true;

            Plane.ToWgs84(new GeoPoint(170, 33), out double lat, out double lon);
            var sim = new GnssSimulator(lat, lon, 0) { SpeedKmh = 8 };
            var c = new SteerController();
            SteerFeedback fb = SteerPgn.ParseFeedback(SteerPgn.BuildFeedback(0, false, false, 0), DateTime.UtcNow);
            bool blocked = false;
            for (int k = 0; k < 200 && c.LastOffReason == SteerOffReason.None; k++)
            {
                g.OnFix(Fix(sim));
                GuidanceSnapshot s = g.GetSnapshot();
                blocked |= s.UTurnBlocked;
                fb.ReceivedUtc = DateTime.UtcNow;
                SteerInputs i = SteerInputs.From(s, true, fb, DateTime.UtcNow);
                if (k == 0) Assert.That(c.TryEngage(i), Is.EqualTo(SteerOffReason.None));
                c.Update(i);
                sim.Step(0.1);
            }
            Assert.That(blocked, Is.True);
            Assert.That(c.LastOffReason, Is.EqualTo(SteerOffReason.TurnDoesNotFit));
            Assert.That(Plane.ToLocal(sim.Latitude, sim.Longitude).Northing, Is.LessThan(196), "stopped at the headland line");
        }
    }
}
