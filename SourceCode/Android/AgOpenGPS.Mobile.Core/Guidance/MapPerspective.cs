using System;

namespace AgOpenGPS.Mobile.Core.Guidance
{
    /// <summary>
    /// The tilted map view: the flat (top-down) drawing leans back around a pivot (the tractor),
    /// like looking ahead from a raised seat. Everything above the pivot is "ahead" and is drawn
    /// smaller the further away it is, so much more of the field fits on screen.
    /// <para>
    /// The camera sits <see cref="Distance"/> pixels in front of the screen, above the pivot. A flat
    /// point at (X, Y) from the pivot (Y up the screen = ahead) appears at
    /// x' = D X / (D + Y sin t), y' = D Y cos t / (D + Y sin t). That is a 3x3 projective matrix,
    /// which Android's Canvas can apply to the whole drawing at once.
    /// </para>
    /// </summary>
    public sealed class MapPerspective
    {
        public const double MaxTiltDegrees = 60;

        /// <param name="tiltDegrees">0 = flat, top-down; up to <see cref="MaxTiltDegrees"/>.</param>
        /// <param name="pivotX">Screen point that stays where it is (the tractor).</param>
        /// <param name="distance">Camera distance in pixels; larger = weaker perspective. About 1.5 screen heights looks natural.</param>
        public MapPerspective(double tiltDegrees, double pivotX, double pivotY, double distance)
        {
            Tilt = Math.Max(0, Math.Min(MaxTiltDegrees, tiltDegrees));
            PivotX = pivotX;
            PivotY = pivotY;
            Distance = Math.Max(1, distance);
            double t = Tilt * Math.PI / 180.0;
            _sin = Math.Sin(t);
            _cos = Math.Cos(t);
        }

        private readonly double _sin, _cos;

        public double Tilt { get; }
        public double PivotX { get; }
        public double PivotY { get; }
        public double Distance { get; }
        public bool IsFlat => Tilt < 0.01;

        /// <summary>
        /// Screen height above the pivot where the horizon would be (infinitely far ahead), pixels.
        /// Infinite when flat.
        /// </summary>
        public double HorizonAbovePivot => _sin < 1e-9 ? double.PositiveInfinity : Distance * _cos / _sin;

        /// <summary>Flat drawing point to screen point.</summary>
        public void ToScreen(double fx, double fy, out double sx, out double sy)
        {
            double X = fx - PivotX, Y = PivotY - fy;
            double w = Distance + Y * _sin;
            if (w < 1e-6) w = 1e-6; // behind the camera: never drawn sensibly, keep it finite
            sx = PivotX + Distance * X / w;
            sy = PivotY - Distance * Y * _cos / w;
        }

        /// <summary>
        /// Screen point back to the flat drawing. False above the horizon (there is no ground
        /// there); then fx, fy are a point very far ahead in that direction.
        /// </summary>
        public bool ToFlat(double sx, double sy, out double fx, out double fy)
        {
            double xs = sx - PivotX, ys = PivotY - sy;
            double den = Distance * _cos - ys * _sin;
            bool ok = den > 1e-6;
            if (!ok) den = 1e-6;
            double Y = ys * Distance / den;
            double X = xs * (Distance + Y * _sin) / Distance;
            fx = PivotX + X;
            fy = PivotY - Y;
            return ok;
        }

        /// <summary>
        /// The matrix for Android's Matrix.SetValues (row major: scaleX, skewX, transX, skewY,
        /// scaleY, transY, persp0, persp1, persp2), mapping flat drawing to screen.
        /// </summary>
        public float[] MatrixValues()
        {
            double D = Distance, cx = PivotX, cy = PivotY, s = _sin, c = _cos;
            // w = D + (cy - fy) s;  sx w = cx w + D (fx - cx);  sy w = cy w - D (cy - fy) c.
            // Divided by the last value so it is 1, the usual form for a perspective matrix.
            double k = 1.0 / (D + cy * s);
            return new[]
            {
                (float)(D * k), (float)(-cx * s * k), (float)(cx * cy * s * k),
                0f, (float)((D * c - cy * s) * k), (float)((cy * D + cy * cy * s - D * cy * c) * k),
                0f, (float)(-s * k), 1f,
            };
        }
    }
}
