using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// Round compass button, as in map apps: the red needle points to north on the map, so it
    /// turns with the map. Tapping it is handled by the screen (north up / heading up).
    /// </summary>
    public sealed class CompassView : View
    {
        private readonly Paint _disc = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#EEFFFFFF") };
        private readonly Paint _ring = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#90A4AE") };
        private readonly Paint _north = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#E53935") };
        private readonly Paint _south = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#78909C") };
        private readonly Paint _letter = new Paint(PaintFlags.AntiAlias) { Color = Color.ParseColor("#E53935"), TextAlign = Paint.Align.Center, FakeBoldText = true };
        private double _rotation;
        private bool _northUp;

        public CompassView(Context context) : base(context)
        {
            float d = context.Resources.DisplayMetrics.Density;
            _ring.SetStyle(Paint.Style.Stroke);
            _ring.StrokeWidth = 2 * d;
            _letter.TextSize = 11 * d;
            Clickable = true;
        }

        /// <summary>Direction the map shows as up (degrees true), and whether north up is fixed.</summary>
        public void Show(double mapRotation, bool northUp)
        {
            if (Math.Abs(mapRotation - _rotation) < 0.2 && northUp == _northUp) return;
            _rotation = mapRotation;
            _northUp = northUp;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float cx = Width / 2f, cy = Height / 2f, r = Math.Min(cx, cy) - 2;
            _ring.Color = Color.ParseColor(_northUp ? "#1E88E5" : "#90A4AE");
            canvas.DrawCircle(cx, cy, r, _disc);
            canvas.DrawCircle(cx, cy, r, _ring);

            canvas.Save();
            canvas.Rotate((float)-_rotation, cx, cy); // north is "rotation" degrees to the left of up
            float len = r * 0.62f, w = r * 0.22f;
            using (var n = new Path())
            {
                n.MoveTo(cx, cy - len);
                n.LineTo(cx + w, cy);
                n.LineTo(cx - w, cy);
                n.Close();
                canvas.DrawPath(n, _north);
            }
            using (var sp = new Path())
            {
                sp.MoveTo(cx, cy + len);
                sp.LineTo(cx + w, cy);
                sp.LineTo(cx - w, cy);
                sp.Close();
                canvas.DrawPath(sp, _south);
            }
            canvas.DrawText("N", cx, cy - len - 2, _letter);
            canvas.Restore();
        }
    }
}
