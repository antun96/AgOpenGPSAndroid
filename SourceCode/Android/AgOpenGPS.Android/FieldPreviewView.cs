using System.Collections.Generic;
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Views;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// A small north-up map of a field without the satellite picture: boundary, guidance lines and
    /// (for a job) the strip worked, drawn at the implement width. For the Records screen.
    /// </summary>
    public sealed class FieldPreviewView : View
    {
        private readonly Paint _fill = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#2E3B2F") };
        private readonly Paint _edge = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#FFEB3B"), StrokeWidth = 2 };
        private readonly Paint _line = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#42A5F5"), StrokeWidth = 2 };
        private readonly Paint _strip = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(150, 102, 187, 106) };
        private readonly Paint _text = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#B0BEC5"), TextAlign = Paint.Align.Center };
        private List<TrackPoint> _boundary = new List<TrackPoint>();
        private List<List<TrackPoint>> _lines = new List<List<TrackPoint>>();
        private List<List<TrackPoint>> _tracks = new List<List<TrackPoint>>();
        private double _stripWidth;

        public FieldPreviewView(Context context) : base(context)
        {
            float d = context.Resources.DisplayMetrics.Density;
            _edge.SetStyle(Paint.Style.Stroke);
            _edge.StrokeWidth *= d;
            _line.SetStyle(Paint.Style.Stroke);
            _line.StrokeWidth *= d;
            _strip.SetStyle(Paint.Style.Stroke);
            _strip.StrokeCap = Paint.Cap.Butt;
            _strip.StrokeJoin = Paint.Join.Round;
            _text.TextSize = 14 * d;
            SetBackgroundColor(Color.ParseColor("#1B1F23"));
        }

        /// <summary>What to draw: the boundary, the lines (recorded points) and job tracks at <paramref name="stripWidth"/> metres.</summary>
        public void Show(List<TrackPoint> boundary, IEnumerable<AbLine> lines, IEnumerable<TrackPiece> tracks = null, double stripWidth = 0)
        {
            _boundary = boundary ?? new List<TrackPoint>();
            _lines = (lines ?? Enumerable.Empty<AbLine>()).Select(l => l.Points.ToList()).Where(p => p.Count >= 2).ToList();
            _tracks = (tracks ?? Enumerable.Empty<TrackPiece>()).Select(p => p.Points).Where(p => p.Count >= 2).ToList();
            _stripWidth = stripWidth;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var all = _boundary.Concat(_tracks.SelectMany(t => t)).Concat(_lines.SelectMany(l => l)).ToList();
            if (all.Count < 2)
            {
                canvas.DrawText(Tr.T("No boundary, line or track yet"), Width / 2f, Height / 2f, _text);
                return;
            }
            var plane = new LocalPlane(all[0].Latitude, all[0].Longitude);
            List<GeoPoint> Local(IEnumerable<TrackPoint> pts) => pts.Select(p => plane.ToLocal(p.Latitude, p.Longitude)).ToList();
            List<GeoPoint> edge = Local(_boundary);
            List<List<GeoPoint>> tracks = _tracks.Select(Local).ToList();
            List<List<GeoPoint>> lines = _lines.Select(Local).ToList();
            var pts = edge.Concat(tracks.SelectMany(t => t)).Concat(lines.SelectMany(l => l)).ToList();
            double minN = pts.Min(p => p.Northing), maxN = pts.Max(p => p.Northing), minE = pts.Min(p => p.Easting), maxE = pts.Max(p => p.Easting);
            float margin = 10 * Resources.DisplayMetrics.Density;
            double scale = System.Math.Min((Width - 2 * margin) / System.Math.Max(1, maxE - minE), (Height - 2 * margin) / System.Math.Max(1, maxN - minN));
            float ox = (float)((Width - (maxE - minE) * scale) / 2), oy = (float)((Height - (maxN - minN) * scale) / 2);

            Path ToPath(List<GeoPoint> p, bool closed)
            {
                var path = new Path();
                for (int i = 0; i < p.Count; i++)
                {
                    float x = ox + (float)((p[i].Easting - minE) * scale), y = oy + (float)((maxN - p[i].Northing) * scale);
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                if (closed) path.Close();
                return path;
            }

            if (edge.Count >= 3)
                using (Path p = ToPath(edge, true))
                {
                    _fill.SetStyle(Paint.Style.Fill);
                    canvas.DrawPath(p, _fill);
                    canvas.DrawPath(p, _edge);
                }
            _strip.StrokeWidth = (float)System.Math.Max(2, _stripWidth * scale);
            foreach (List<GeoPoint> t in tracks) using (Path p = ToPath(t, false)) canvas.DrawPath(p, _strip);
            foreach (List<GeoPoint> l in lines) using (Path p = ToPath(l, false)) canvas.DrawPath(p, _line);
        }
    }
}
