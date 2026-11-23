using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System;
using Android.Graphics;
using Android.Graphics.Pdf;
using Path = Android.Graphics.Path;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// The job log as a printable PDF (A4 landscape), in the app's language: a table of all jobs,
    /// then a page per job with a map of the field and the strip worked (from the job's track).
    /// </summary>
    public static class JobReport
    {
        private const int PageW = 842, PageH = 595, Margin = 36, RowH = 18;

        /// <param name="track">The job's track, or null to leave the map pages out.</param>
        /// <param name="boundary">The job's field boundary (may be empty).</param>
        public static byte[] Pdf(IEnumerable<Job> jobsNewestFirst, Func<Job, List<TrackPiece>> track = null,
            Func<Job, List<TrackPoint>> boundary = null)
        {
            List<Job> jobs = jobsNewestFirst.OrderBy(j => j.StartUtc).ToList();
            var ci = CultureInfo.InvariantCulture;
            string[] head =
            {
                Tr.T("Date"), Tr.T("Field"), Tr.T("Start"), Tr.T("End"), Tr.T("Working time (h:mm)"),
                Tr.T("Worked (ha)"), Tr.T("Distance (km)"), Tr.T("Average speed (km/h)"), Tr.T("Implement width (m)"),
            };
            float[] colX = { 0, 70, 250, 300, 350, 460, 540, 625, 735 };

            var title = new Paint { Color = Color.Black, TextSize = 18, FakeBoldText = true, AntiAlias = true };
            var bold = new Paint { Color = Color.Black, TextSize = 10, FakeBoldText = true, AntiAlias = true };
            var text = new Paint { Color = Color.Black, TextSize = 10, AntiAlias = true };
            var line = new Paint { Color = Color.Gray, StrokeWidth = 0.5f };

            using (var doc = new PdfDocument())
            {
                int pageNo = 0, row = 0;
                PdfDocument.Page page = null;
                Canvas c = null;
                float y = 0;

                void NewPage()
                {
                    if (page != null) doc.FinishPage(page);
                    pageNo++;
                    page = doc.StartPage(new PdfDocument.PageInfo.Builder(PageW, PageH, pageNo).Create());
                    c = page.Canvas;
                    y = Margin + 18;
                    c.DrawText(Tr.T("Job log") + "  –  AgOpenGPS", Margin, y, title);
                    y += 16;
                    c.DrawText(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm", ci) + "   " + Tr.F("Page {0}", pageNo), Margin, y, text);
                    y += 24;
                    for (int i = 0; i < head.Length; i++) c.DrawText(head[i], Margin + colX[i], y, bold);
                    y += 6;
                    c.DrawLine(Margin, y, PageW - Margin, y, line);
                    y += RowH - 4;
                }

                NewPage();
                double totalHa = 0, totalSec = 0;
                foreach (Job j in jobs)
                {
                    if (y > PageH - Margin - RowH * 2) NewPage();
                    System.DateTime s = j.StartUtc.ToLocalTime(), e = j.EndUtc.ToLocalTime();
                    string[] cells =
                    {
                        s.ToString("yyyy-MM-dd", ci), Fit(string.IsNullOrEmpty(j.WorkType) ? j.FieldName : j.FieldName + " · " + j.WorkType, 30),
                        s.ToString("HH:mm", ci), e.ToString("HH:mm", ci),
                        JobLog.Duration(j.WorkingSeconds), j.Hectares.ToString("0.00", ci), (j.WorkingMeters / 1000).ToString("0.00", ci),
                        double.IsNaN(j.AverageSpeedKmh) ? "" : j.AverageSpeedKmh.ToString("0.0", ci), j.ImplementWidth.ToString("0.00", ci),
                    };
                    for (int i = 0; i < cells.Length; i++) c.DrawText(cells[i], Margin + colX[i], y, text);
                    y += RowH;
                    row++;
                    totalHa += j.Hectares;
                    totalSec += j.WorkingSeconds;
                }
                y += 4;
                c.DrawLine(Margin, y - RowH + 6, PageW - Margin, y - RowH + 6, line);
                c.DrawText(Tr.F("Total: {0} jobs, {1:0.00} ha, {2} h:mm", row, totalHa, JobLog.Duration(totalSec)), Margin, y + 4, bold);
                doc.FinishPage(page);

                if (track != null)
                {
                    foreach (Job j in jobs)
                    {
                        List<TrackPiece> pieces = track(j);
                        if (pieces == null || pieces.Count == 0) continue;
                        pageNo++;
                        page = doc.StartPage(new PdfDocument.PageInfo.Builder(PageW, PageH, pageNo).Create());
                        DrawJobMap(page.Canvas, j, pieces, boundary?.Invoke(j) ?? new List<TrackPoint>(), title, text, pageNo);
                        doc.FinishPage(page);
                    }
                }

                using (var ms = new MemoryStream())
                {
                    doc.WriteTo(ms);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>One job on one page: the field boundary and the strip worked, at the implement width, north up.</summary>
        private static void DrawJobMap(Canvas c, Job j, List<TrackPiece> pieces, List<TrackPoint> boundary,
            Paint title, Paint text, int pageNo)
        {
            var ci = CultureInfo.InvariantCulture;
            System.DateTime s = j.StartUtc.ToLocalTime(), e = j.EndUtc.ToLocalTime();
            float y = Margin + 18;
            c.DrawText(s.ToString("yyyy-MM-dd", ci) + "   " + j.FieldName + (string.IsNullOrEmpty(j.WorkType) ? "" : "   " + j.WorkType), Margin, y, title);
            y += 16;
            c.DrawText(s.ToString("HH:mm", ci) + " - " + e.ToString("HH:mm", ci) + "   " + TrackExport.Summary(j, Tr.T)
                + "   " + Tr.F("Page {0}", pageNo), Margin, y, text);
            if (!string.IsNullOrEmpty(j.Note))
            {
                // The note, wrapped to the page width (at most three lines here).
                int lines = 0;
                foreach (string part in Wrap(j.Note, text, PageW - 2 * Margin))
                {
                    if (++lines > 3) break;
                    y += 13;
                    c.DrawText(part, Margin, y, text);
                }
            }

            // Everything on one local plane, fitted into the area below the heading.
            TrackPoint first = pieces[0].Points[0];
            var plane = new LocalPlane(first.Latitude, first.Longitude);
            var track = pieces.Select(p => p.Points.Select(q => plane.ToLocal(q.Latitude, q.Longitude)).ToList()).ToList();
            var edge = boundary.Select(b => plane.ToLocal(b.Latitude, b.Longitude)).ToList();
            var all = track.SelectMany(p => p).Concat(edge).ToList();
            double minN = all.Min(p => p.Northing), maxN = all.Max(p => p.Northing);
            double minE = all.Min(p => p.Easting), maxE = all.Max(p => p.Easting);
            double half = System.Math.Max(1.0, j.ImplementWidth / 2);
            minN -= half; maxN += half; minE -= half; maxE += half;

            float left = Margin, top = y + 18, right = PageW - Margin, bottom = PageH - Margin - 14;
            double scale = System.Math.Min((right - left) / System.Math.Max(1, maxE - minE), (bottom - top) / System.Math.Max(1, maxN - minN));
            float ox = left + (float)((right - left - (maxE - minE) * scale) / 2), oy = top + (float)((bottom - top - (maxN - minN) * scale) / 2);
            float X(GeoPoint p) => ox + (float)((p.Easting - minE) * scale);
            float Y(GeoPoint p) => oy + (float)((maxN - p.Northing) * scale);

            Path Line(List<GeoPoint> pts, bool closed)
            {
                var path = new Path();
                for (int i = 0; i < pts.Count; i++)
                {
                    if (i == 0) path.MoveTo(X(pts[i]), Y(pts[i]));
                    else path.LineTo(X(pts[i]), Y(pts[i]));
                }
                if (closed) path.Close();
                return path;
            }

            if (edge.Count >= 3)
            {
                using (var fill = new Paint { Color = Color.Argb(255, 245, 245, 240), AntiAlias = true })
                using (var stroke = new Paint { Color = Color.DarkGray, StrokeWidth = 1.5f, AntiAlias = true })
                using (Path p = Line(edge, true))
                {
                    fill.SetStyle(Paint.Style.Fill);
                    stroke.SetStyle(Paint.Style.Stroke);
                    c.DrawPath(p, fill);
                    c.DrawPath(p, stroke);
                }
            }

            // The strip worked: the track drawn as wide as the implement.
            using (var strip = new Paint { Color = Color.Argb(150, 76, 175, 80), StrokeWidth = (float)System.Math.Max(0.8, j.ImplementWidth * scale), AntiAlias = true })
            using (var centre = new Paint { Color = Color.Argb(255, 27, 94, 32), StrokeWidth = 0.6f, AntiAlias = true })
            {
                strip.SetStyle(Paint.Style.Stroke);
                strip.StrokeCap = Paint.Cap.Butt;
                strip.StrokeJoin = Paint.Join.Round;
                centre.SetStyle(Paint.Style.Stroke);
                foreach (List<GeoPoint> piece in track)
                    using (Path p = Line(piece, false))
                    {
                        c.DrawPath(p, strip);
                        c.DrawPath(p, centre);
                    }
            }

            // North arrow and a scale bar.
            using (var ink = new Paint { Color = Color.Black, StrokeWidth = 1.2f, AntiAlias = true, TextSize = 10 })
            {
                float nx = right - 14, ny = top + 6;
                c.DrawLine(nx, ny + 26, nx, ny, ink);
                c.DrawLine(nx, ny, nx - 4, ny + 7, ink);
                c.DrawLine(nx, ny, nx + 4, ny + 7, ink);
                c.DrawText("N", nx - 3, ny + 38, ink);

                double target = (right - left) / 5 / scale;              // about a fifth of the width, in metres
                double bar = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(System.Math.Max(1, target))));
                if (bar * 5 <= target) bar *= 5; else if (bar * 2 <= target) bar *= 2;
                float bx = left, by = PageH - Margin;
                c.DrawLine(bx, by, bx + (float)(bar * scale), by, ink);
                c.DrawLine(bx, by - 3, bx, by + 3, ink);
                c.DrawLine(bx + (float)(bar * scale), by - 3, bx + (float)(bar * scale), by + 3, ink);
                c.DrawText(bar >= 1000 ? (bar / 1000).ToString("0.#", ci) + " km" : bar.ToString("0", ci) + " m", bx + (float)(bar * scale) + 6, by + 4, ink);
            }
        }

        private static IEnumerable<string> Wrap(string s, Paint paint, float width)
        {
            foreach (string para in s.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (string word in para.Split(' '))
                {
                    string next = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && paint.MeasureText(next) > width)
                    {
                        yield return line;
                        line = word;
                    }
                    else line = next;
                }
                yield return line;
            }
        }

        private static string Fit(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max - 1) + "…";
    }
}
