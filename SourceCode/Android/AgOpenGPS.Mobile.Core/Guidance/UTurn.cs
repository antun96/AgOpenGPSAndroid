using System;
using System.Collections.Generic;

namespace AgOpenGPS.Mobile.Core.Guidance
{
    /// <summary>A planned turn at the headland: the path to follow, on the line's plane.</summary>
    public sealed class UTurnPlan
    {
        /// <summary>
        /// Lead-in along the current pass, the turn, then a lead-out along the next pass back into
        /// the field. Points about every half metre, in driving order.
        /// </summary>
        public List<GeoPoint> Path = new List<GeoPoint>();
        /// <summary>Where the turn leaves the current pass (the pass crosses the headland line here).</summary>
        public GeoPoint Start;
        /// <summary>Where the turn joins the next pass.</summary>
        public GeoPoint End;
        /// <summary>Distance from the vehicle along the pass to <see cref="Start"/>, metres.</summary>
        public double DistanceToStart;
        /// <summary>Distance along <see cref="Path"/> where the lead-out starts.</summary>
        public double LeadOutStartsAt;
        /// <summary>The whole turn stays inside the field boundary (with a margin).</summary>
        public bool Fits;
        public bool TurnRight;
    }

    /// <summary>
    /// The automatic headland turn ("U-turn" in AgOpenGPS): where the current pass meets the
    /// headland line, a turn through the headland into the next pass (or one further, with skips).
    /// Passes far enough apart get a flat turn (quarter circle, straight, quarter circle); closer
    /// ones a bulb turn that first swings the other way, as a tractor with a large turning circle
    /// has to.
    /// </summary>
    public static class UTurn
    {
        /// <summary>Lead-in and lead-out length along the passes, metres.</summary>
        public const double LeadMeters = 20;
        /// <summary>The turn must stay this far inside the boundary.</summary>
        public const double BoundaryMarginMeters = 1.0;
        private const double StepMeters = 0.5;

        /// <summary>How far past the start the turn reaches, metres (it must fit in the headland).</summary>
        public static double Depth(double spacing, double radius)
        {
            if (spacing >= 2 * radius) return radius;
            double c = spacing / 2 + radius;
            return Math.Sqrt(Math.Max(0, 4 * radius * radius - c * c)) + radius;
        }

        /// <summary>
        /// The turn alone, from <paramref name="start"/> driving at <paramref name="headingDeg"/>, to
        /// the pass <paramref name="spacing"/> metres to the right (or left), driving back.
        /// </summary>
        public static List<GeoPoint> Shape(GeoPoint start, double headingDeg, bool turnRight, double spacing, double radius)
        {
            var local = new List<(double X, double Y)>(); // x = towards the next pass, y = forward
            double R = Math.Max(0.5, radius), D = Math.Max(0.1, spacing);

            void Arc(double cx, double cy, double from, double to)
            {
                int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(to - from) * R / StepMeters));
                for (int i = 0; i <= n; i++)
                {
                    double a = from + (to - from) * i / n;
                    local.Add((cx + R * Math.Cos(a), cy + R * Math.Sin(a)));
                }
            }

            if (D >= 2 * R)
            {
                Arc(R, 0, Math.PI, Math.PI / 2);          // quarter circle into the headland
                int n = Math.Max(1, (int)Math.Ceiling((D - 2 * R) / StepMeters));
                for (int i = 1; i < n; i++) local.Add((R + (D - 2 * R) * i / n, R));
                Arc(D - R, 0, Math.PI / 2, 0);            // quarter circle onto the next pass
            }
            else
            {
                // Bulb: swing away round C1, the big arc round C2, back round C3 (all radius R, touching).
                double c = D / 2 + R, h = Math.Sqrt(Math.Max(0, 4 * R * R - c * c));
                double a1 = Math.Atan2(h, c);
                Arc(-R, 0, 0, a1);
                Arc(D / 2, h, a1 + Math.PI, -a1);
                Arc(D + R, 0, Math.PI - a1, Math.PI);
            }

            double hr = LocalPlane.ToRad(headingDeg);
            double fe = Math.Sin(hr), fn = Math.Cos(hr), re = Math.Cos(hr), rn = -Math.Sin(hr);
            double side = turnRight ? 1 : -1;
            var pts = new List<GeoPoint>(local.Count);
            foreach ((double x, double y) in local)
            {
                double sx = x * side;
                pts.Add(new GeoPoint(start.Northing + sx * rn + y * fn, start.Easting + sx * re + y * fe));
            }
            return pts;
        }

        /// <summary>
        /// Plans the turn at the end of the current pass. <paramref name="pass"/> is the current pass in
        /// driving order, <paramref name="nextPass"/> the pass to turn into, in its driving order (the
        /// other way). Null when the pass does not reach the headland line ahead.
        /// </summary>
        public static UTurnPlan Plan(IReadOnlyList<GeoPoint> pass, IReadOnlyList<GeoPoint> nextPass,
            IReadOnlyList<GeoPoint> headland, IReadOnlyList<GeoPoint> boundary, GeoPoint vehicle,
            bool turnRight, double spacing, double radius)
        {
            if (pass == null || pass.Count < 2 || nextPass == null || nextPass.Count < 2 || headland == null || headland.Count < 3) return null;

            double vAlong = Along(pass, vehicle, out _);

            // The first place ahead where the pass leaves the main part (crosses the headland line outwards).
            double best = double.MaxValue;
            GeoPoint start = default;
            double startHeading = 0;
            double along = 0;
            for (int i = 0; i < pass.Count - 1; i++)
            {
                GeoPoint a = pass[i], b = pass[i + 1];
                double len = a.DistanceTo(b);
                if (len < 1e-9) continue;
                for (int j = 0, k = headland.Count - 1; j < headland.Count; k = j++)
                {
                    if (!Intersect(a, b, headland[k], headland[j], out double t)) continue;
                    double s = along + t * len;
                    if (s < vAlong - 1 || s >= best) continue;
                    // Outwards: just after the crossing the pass is outside the main part.
                    double tt = Math.Min(1, t + 0.05 / len);
                    var after = new GeoPoint(a.Northing + (b.Northing - a.Northing) * tt, a.Easting + (b.Easting - a.Easting) * tt);
                    if (Geometry.Contains(headland, after)) continue;
                    best = s;
                    start = new GeoPoint(a.Northing + (b.Northing - a.Northing) * t, a.Easting + (b.Easting - a.Easting) * t);
                    startHeading = LocalPlane.Wrap360(LocalPlane.ToDeg(Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
                }
                along += len;
            }
            if (best == double.MaxValue) return null;

            var plan = new UTurnPlan { Start = start, DistanceToStart = best - vAlong, TurnRight = turnRight };
            List<GeoPoint> turn = Shape(start, startHeading, turnRight, spacing, radius);
            plan.End = turn[turn.Count - 1];

            plan.Fits = true;
            if (boundary != null && boundary.Count >= 3)
            {
                foreach (GeoPoint p in turn)
                {
                    if (!Geometry.Contains(boundary, p) || Geometry.DistanceToEdge(boundary, p) < BoundaryMarginMeters)
                    {
                        plan.Fits = false;
                        break;
                    }
                }
            }

            // Lead-in along the current pass, the turn, lead-out along the next pass.
            plan.Path.AddRange(Slice(pass, Math.Max(0, Math.Min(vAlong - 2, best - LeadMeters)), best));
            plan.Path.AddRange(turn);
            plan.LeadOutStartsAt = Length(plan.Path);
            double endAlong = Along(nextPass, plan.End, out _);
            List<GeoPoint> leadOut = Slice(nextPass, endAlong, endAlong + LeadMeters);
            if (leadOut.Count > 0) leadOut.RemoveAt(0); // the turn already ends there
            plan.Path.AddRange(leadOut);
            return plan;
        }

        /// <summary>Distance along <paramref name="line"/> to the point nearest <paramref name="p"/>; also how far off it is.</summary>
        public static double Along(IReadOnlyList<GeoPoint> line, GeoPoint p, out double offset)
        {
            double bestD2 = double.MaxValue, bestAlong = 0, along = 0;
            for (int i = 0; i < line.Count - 1; i++)
            {
                GeoPoint a = line[i], b = line[i + 1];
                double de = b.Easting - a.Easting, dn = b.Northing - a.Northing;
                double l2 = de * de + dn * dn;
                double len = Math.Sqrt(l2);
                double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.Easting - a.Easting) * de + (p.Northing - a.Northing) * dn) / l2));
                double pe = a.Easting + de * t - p.Easting, pn = a.Northing + dn * t - p.Northing;
                double d2 = pe * pe + pn * pn;
                if (d2 < bestD2) { bestD2 = d2; bestAlong = along + t * len; }
                along += len;
            }
            offset = bestD2 == double.MaxValue ? double.NaN : Math.Sqrt(bestD2);
            return bestAlong;
        }

        public static double Length(IReadOnlyList<GeoPoint> line)
        {
            double l = 0;
            for (int i = 0; i < line.Count - 1; i++) l += line[i].DistanceTo(line[i + 1]);
            return l;
        }

        /// <summary>The part of <paramref name="line"/> from <paramref name="from"/> to <paramref name="to"/> metres along it.</summary>
        public static List<GeoPoint> Slice(IReadOnlyList<GeoPoint> line, double from, double to)
        {
            var r = new List<GeoPoint>();
            double along = 0;
            for (int i = 0; i < line.Count - 1 && along <= to; i++)
            {
                GeoPoint a = line[i], b = line[i + 1];
                double len = a.DistanceTo(b);
                if (len < 1e-9) continue;
                double s0 = Math.Max(from, along), s1 = Math.Min(to, along + len);
                if (s1 >= s0)
                {
                    double t0 = (s0 - along) / len, t1 = (s1 - along) / len;
                    GeoPoint p0 = new GeoPoint(a.Northing + (b.Northing - a.Northing) * t0, a.Easting + (b.Easting - a.Easting) * t0);
                    if (r.Count == 0 || r[r.Count - 1].DistanceTo(p0) > 1e-6) r.Add(p0);
                    r.Add(new GeoPoint(a.Northing + (b.Northing - a.Northing) * t1, a.Easting + (b.Easting - a.Easting) * t1));
                }
                along += len;
            }
            return r;
        }

        /// <summary>Segment a-b crosses segment c-d; t is the position on a-b (0..1).</summary>
        private static bool Intersect(GeoPoint a, GeoPoint b, GeoPoint c, GeoPoint d, out double t)
        {
            t = 0;
            double rE = b.Easting - a.Easting, rN = b.Northing - a.Northing;
            double sE = d.Easting - c.Easting, sN = d.Northing - c.Northing;
            double den = rE * sN - rN * sE;
            if (Math.Abs(den) < 1e-12) return false;
            double qE = c.Easting - a.Easting, qN = c.Northing - a.Northing;
            t = (qE * sN - qN * sE) / den;
            double u = (qE * rN - qN * rE) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }
    }
}
