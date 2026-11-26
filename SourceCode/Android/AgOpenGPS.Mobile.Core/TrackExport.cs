using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>A job with its track, for export.</summary>
    public sealed class JobTrack
    {
        public Job Job;
        public List<TrackPiece> Pieces = new List<TrackPiece>();
        public bool HasTrack => Pieces.Any(p => p.Points.Count >= 2);
    }

    /// <summary>
    /// Job tracks as files other programs read: KML (Google Earth, QGIS) and GeoJSON (QGIS, web maps,
    /// farm software). One line per piece of track (a new piece after each pause), with its start
    /// and end time and the job's figures as properties.
    /// </summary>
    public static class TrackExport
    {
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        /// <param name="t">Translates the English labels.</param>
        public static string ToKml(IEnumerable<JobTrack> jobs, Func<string, string> t = null)
        {
            t ??= s => s;
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<kml xmlns=\"http://www.opengis.net/kml/2.2\">\n<Document>\n");
            sb.Append("  <name>").Append(X(t("Job log"))).Append("</name>\n");
            sb.Append("  <Style id=\"track\"><LineStyle><color>ff3cb44b</color><width>3</width></LineStyle></Style>\n");
            foreach (JobTrack jt in jobs.Where(j => j.HasTrack).OrderBy(j => j.Job.StartUtc))
            {
                Job j = jt.Job;
                DateTime s = j.StartUtc.ToLocalTime(), e = j.EndUtc.ToLocalTime();
                string what = string.IsNullOrEmpty(j.WorkType) ? "" : "  " + j.WorkType;
                sb.Append("  <Folder>\n    <name>").Append(X(s.ToString("yyyy-MM-dd HH:mm", Ci) + "  " + j.FieldName + what)).Append("</name>\n");
                sb.Append("    <description>").Append(X(Summary(j, t) + (string.IsNullOrEmpty(j.Note) ? "" : "\n" + j.Note))).Append("</description>\n");
                sb.Append("    <TimeSpan><begin>").Append(Iso(j.StartUtc)).Append("</begin><end>").Append(Iso(j.EndUtc)).Append("</end></TimeSpan>\n");
                int n = 0;
                foreach (TrackPiece piece in jt.Pieces.Where(p => p.Points.Count >= 2))
                {
                    n++;
                    sb.Append("    <Placemark>\n      <name>").Append(X(n.ToString(Ci) + ": " + piece.StartUtc.ToLocalTime().ToString("HH:mm", Ci)
                        + " - " + piece.EndUtc.ToLocalTime().ToString("HH:mm", Ci))).Append("</name>\n");
                    sb.Append("      <TimeSpan><begin>").Append(Iso(piece.StartUtc)).Append("</begin><end>").Append(Iso(piece.EndUtc)).Append("</end></TimeSpan>\n");
                    sb.Append("      <styleUrl>#track</styleUrl>\n      <LineString><tessellate>1</tessellate><coordinates>\n");
                    foreach (TrackPoint p in piece.Points)
                        sb.Append("        ").Append(p.Longitude.ToString("0.0000000", Ci)).Append(',').Append(p.Latitude.ToString("0.0000000", Ci)).Append('\n');
                    sb.Append("      </coordinates></LineString>\n    </Placemark>\n");
                }
                sb.Append("  </Folder>\n");
            }
            sb.Append("</Document>\n</kml>\n");
            return sb.ToString();
        }

        /// <summary>One MultiLineString feature per job; "pieces" holds each line's start and end time (ISO 8601, UTC).</summary>
        public static string ToGeoJson(IEnumerable<JobTrack> jobs)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    w.WriteString("type", "FeatureCollection");
                    w.WriteStartArray("features");
                    foreach (JobTrack jt in jobs.Where(j => j.HasTrack).OrderBy(j => j.Job.StartUtc))
                    {
                        Job j = jt.Job;
                        List<TrackPiece> pieces = jt.Pieces.Where(p => p.Points.Count >= 2).ToList();
                        w.WriteStartObject();
                        w.WriteString("type", "Feature");
                        w.WriteStartObject("properties");
                        w.WriteString("field", j.FieldName ?? "");
                        w.WriteString("workType", j.WorkType ?? "");
                        w.WriteString("implement", j.ImplementName ?? "");
                        w.WriteString("note", j.Note ?? "");
                        w.WriteString("start", Iso(j.StartUtc));
                        w.WriteString("end", Iso(j.EndUtc));
                        w.WriteNumber("workingHours", Math.Round(j.WorkingSeconds / 3600.0, 3));
                        w.WriteNumber("workedHectares", Math.Round(j.Hectares, 3));
                        w.WriteNumber("distanceKm", Math.Round(j.WorkingMeters / 1000.0, 3));
                        w.WriteNumber("implementWidthM", Math.Round(j.ImplementWidth, 2));
                        w.WriteStartArray("pieces");
                        foreach (TrackPiece p in pieces)
                        {
                            w.WriteStartObject();
                            w.WriteString("start", Iso(p.StartUtc));
                            w.WriteString("end", Iso(p.EndUtc));
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                        w.WriteStartObject("geometry");
                        w.WriteString("type", "MultiLineString");
                        w.WriteStartArray("coordinates");
                        foreach (TrackPiece p in pieces)
                        {
                            w.WriteStartArray();
                            foreach (TrackPoint q in p.Points)
                            {
                                w.WriteStartArray();
                                w.WriteNumberValue(Math.Round(q.Longitude, 7));
                                w.WriteNumberValue(Math.Round(q.Latitude, 7));
                                w.WriteEndArray();
                            }
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>"Working time 2:15, 4.32 ha, 18.40 km, 8.2 km/h, implement 6.00 m" (translated).</summary>
        public static string Summary(Job j, Func<string, string> t)
        {
            string speed = double.IsNaN(j.AverageSpeedKmh) ? "-" : j.AverageSpeedKmh.ToString("0.0", Ci);
            return string.Format(Ci, t("Working time {0}, {1:0.00} ha, {2:0.00} km, {3} km/h, implement {4:0.00} m"),
                JobLog.Duration(j.WorkingSeconds), j.Hectares, j.WorkingMeters / 1000.0, speed, j.ImplementWidth);
        }

        private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", Ci);

        private static string X(string s) => SecurityElement.Escape(s ?? "");
    }
}
