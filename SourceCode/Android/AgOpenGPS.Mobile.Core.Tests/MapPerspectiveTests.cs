using System;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class MapPerspectiveTests
    {
        private const double W = 1200, H = 800, Cx = 600, Cy = 640;

        [Test]
        public void Flat_ChangesNothing()
        {
            var p = new MapPerspective(0, Cx, Cy, 1.5 * H);
            Assert.That(p.IsFlat, Is.True);
            float[] m = p.MatrixValues();
            Assert.That(m, Is.EqualTo(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }).Within(1e-6f), "identity");
            p.ToScreen(100, 50, out double x, out double y);
            Assert.That(x, Is.EqualTo(100).Within(1e-9));
            Assert.That(y, Is.EqualTo(50).Within(1e-9));
        }

        [Test]
        public void Tilted_PivotStays_AheadShrinks_BehindGrows()
        {
            var p = new MapPerspective(45, Cx, Cy, 1.5 * H);
            p.ToScreen(Cx, Cy, out double x, out double y);
            Assert.That(x, Is.EqualTo(Cx).Within(1e-9));
            Assert.That(y, Is.EqualTo(Cy).Within(1e-9));

            p.ToScreen(Cx, Cy - 200, out _, out double ahead);   // 200 px ahead in the flat drawing
            p.ToScreen(Cx, Cy + 100, out _, out double behind);  // 100 px behind
            Assert.That(Cy - ahead, Is.LessThan(200), "ahead is drawn closer to the tractor (further away)");
            Assert.That(behind - Cy, Is.GreaterThan(100 * Math.Cos(Math.PI / 4)));

            // Things ahead get narrower: a 100 px wide strip far ahead is drawn less than 100 px wide.
            p.ToScreen(Cx - 50, Cy - 2000, out double l, out _);
            p.ToScreen(Cx + 50, Cy - 2000, out double r, out _);
            Assert.That(r - l, Is.LessThan(100));
        }

        [TestCase(20.0)]
        [TestCase(45.0)]
        [TestCase(60.0)]
        public void ToFlat_IsTheInverse(double tilt)
        {
            var p = new MapPerspective(tilt, Cx, Cy, 1.5 * H);
            foreach (var (fx, fy) in new[] { (0.0, 0.0), (W, -3000.0), (300.0, 700.0), (Cx, Cy - 5000.0) })
            {
                p.ToScreen(fx, fy, out double sx, out double sy);
                Assert.That(p.ToFlat(sx, sy, out double bx, out double by), Is.True);
                Assert.That(bx, Is.EqualTo(fx).Within(1e-6));
                Assert.That(by, Is.EqualTo(fy).Within(1e-6));
            }
        }

        [Test]
        public void MatrixValues_MatchToScreen()
        {
            var p = new MapPerspective(50, Cx, Cy, 1.5 * H);
            float[] m = p.MatrixValues();
            foreach (var (fx, fy) in new[] { (0.0, 0.0), (W, H), (250.0, -900.0) })
            {
                double w = m[6] * fx + m[7] * fy + m[8];
                double sx = (m[0] * fx + m[1] * fy + m[2]) / w;
                double sy = (m[3] * fx + m[4] * fy + m[5]) / w;
                p.ToScreen(fx, fy, out double ex, out double ey);
                Assert.That(sx, Is.EqualTo(ex).Within(0.05));
                Assert.That(sy, Is.EqualTo(ey).Within(0.05));
            }
        }

        [Test]
        public void AtMaxTilt_TheHorizonStaysAboveTheScreen()
        {
            // With the tractor at 80 % of the height and the camera 1.5 heights away, the top edge
            // still shows ground: no empty "sky".
            var p = new MapPerspective(MapPerspective.MaxTiltDegrees, Cx, 0.8 * H, 1.5 * H);
            Assert.That(p.HorizonAbovePivot, Is.GreaterThan(0.8 * H));
            Assert.That(p.ToFlat(0, 0, out _, out double fy), Is.True);
            Assert.That(0.8 * H - fy, Is.GreaterThan(5 * H), "the top of the screen is far ahead");
        }

        [Test]
        public void AboveTheHorizon_IsReported()
        {
            var p = new MapPerspective(60, Cx, Cy, 1.5 * H);
            Assert.That(p.ToFlat(Cx, Cy - p.HorizonAbovePivot - 10, out _, out _), Is.False);
        }
    }
}
