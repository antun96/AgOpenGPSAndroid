using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class TrackLogTests
    {
        private static readonly LocalPlane Plane = new LocalPlane(45.8150, 15.9819);
        private string _dir;
        private DateTime _t;
        private readonly Field _field = new Field { Name = "Njiva \"Gornja\" & <Donja>" };

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aog-track-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _t = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Local).ToUniversalTime();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        /// <summary>Drives north at 10 km/h for <paramref name="seconds"/>, sampled at 4 Hz, from <paramref name="north"/>.</summary>
        private double Drive(JobLog log, double north, double seconds, bool working = true)
        {
            for (int i = 0; i < seconds * 4; i++)
            {
                Plane.ToWgs84(new GeoPoint(north, 0), out double lat, out double lon);
                log.Sample(_t, working, _field, lat, lon, 0, 6);
                _t = _t.AddSeconds(0.25);
                north += 0.25 * 10 / 3.6;
            }
            return north;
        }

        private JobLog NewLog() => new JobLog(Path.Combine(_dir, "jobs.json"));

        [Test]
        public void RecordsAPointAboutEveryMetre_InPiecesBetweenPauses()
        {
            JobLog log = NewLog();
            double n = Drive(log, 0, 30);              // about 83 m
            n = Drive(log, n, 10, working: false);     // headland, Work off
            Drive(log, n, 20);                         // about 56 m
            string id = log.Current.Id;

            List<TrackPiece> pieces = log.Track(id);
            Assert.That(pieces.Count, Is.EqualTo(2));
            // Samples come every 0.69 m, so every second one is kept: a point every 1.39 m.
            Assert.That(pieces[0].Points.Count, Is.InRange(55, 65));
            Assert.That(pieces[1].Points.Count, Is.InRange(36, 44));
            // Only the start and end time of each piece: 30 s, a 10 s pause, then 20 s.
            Assert.That((pieces[0].EndUtc - pieces[0].StartUtc).TotalSeconds, Is.EqualTo(29.75).Within(0.01));
            Assert.That((pieces[1].StartUtc - pieces[0].EndUtc).TotalSeconds, Is.EqualTo(10.25).Within(0.01));
            Assert.That((pieces[1].EndUtc - pieces[1].StartUtc).TotalSeconds, Is.EqualTo(19.75).Within(0.01));
            GeoPoint first = Plane.ToLocal(pieces[0].Points[0].Latitude, pieces[0].Points[0].Longitude);
            Assert.That(first.Northing, Is.EqualTo(0).Within(0.01));
        }

        [Test]
        public void Track_IsSavedAndReadBack_AfterARestart()
        {
            JobLog log = NewLog();
            Drive(log, 0, 20);
            string id = log.Current.Id;
            TrackPiece before = log.Track(id)[0];
            log.EndJob();

            JobLog again = NewLog();
            List<TrackPiece> pieces = again.Track(id);
            Assert.That(pieces.Count, Is.EqualTo(1));
            Assert.That(pieces[0].Points.Count, Is.EqualTo(before.Points.Count));
            Assert.That(pieces[0].StartUtc, Is.EqualTo(before.StartUtc).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(pieces[0].EndUtc, Is.EqualTo(before.EndUtc).Within(TimeSpan.FromMilliseconds(1)));

            // The file holds no time per point: only "#start", "lat,lon" lines and "=end".
            string[] lines = File.ReadAllLines(Path.Combine(_dir, "tracks", id + ".trk"));
            Assert.That(lines.First(), Does.StartWith("#"));
            Assert.That(lines.Last(), Does.StartWith("="));
            Assert.That(lines.Skip(1).Take(lines.Length - 2).All(l => l.Split(',').Length == 2), Is.True);
        }

        [Test]
        public void StandingStill_AddsNoPoints_ButTheEndTimeMovesOn()
        {
            JobLog log = NewLog();
            double n = Drive(log, 0, 5);
            Plane.ToWgs84(new GeoPoint(n, 0), out double lat, out double lon);
            int before = log.Track(log.Current.Id)[0].Points.Count;
            DateTime stop = _t;
            for (int i = 0; i < 4 * 60; i++)
            {
                log.Sample(_t, true, _field, lat, lon, 0, 6);
                _t = _t.AddSeconds(0.25);
            }
            TrackPiece piece = log.Track(log.Current.Id)[0];
            Assert.That(piece.Points.Count, Is.LessThanOrEqualTo(before + 1));
            Assert.That((piece.EndUtc - stop).TotalSeconds, Is.EqualTo(59.75).Within(0.01));
        }

        [Test]
        public void OldTracks_WithATimeOnEveryPoint_AreStillRead()
        {
            var pieces = new List<TrackPiece>();
            JobLog.ParseTrack("#\n1700000000000,45.1,16.1\n1700000005000,45.2,16.2\n", pieces);
            Assert.That(pieces.Count, Is.EqualTo(1));
            Assert.That(pieces[0].Points.Count, Is.EqualTo(2));
            Assert.That((pieces[0].EndUtc - pieces[0].StartUtc).TotalSeconds, Is.EqualTo(5));
        }

        [Test]
        public void SignalGap_StartsANewPiece()
        {
            JobLog log = NewLog();
            double n = Drive(log, 0, 10);
            _t = _t.AddSeconds(30); // no samples for 30 s
            Drive(log, n + 80, 10);
            Assert.That(log.Track(log.Current.Id).Count, Is.EqualTo(2));
        }

        [Test]
        public void DeletingTheJob_DeletesItsTrack()
        {
            JobLog log = NewLog();
            Drive(log, 0, 20);
            string id = log.Current.Id;
            log.EndJob();
            Assert.That(File.Exists(Path.Combine(_dir, "tracks", id + ".trk")), Is.True);
            log.Delete(id);
            Assert.That(File.Exists(Path.Combine(_dir, "tracks", id + ".trk")), Is.False);
            Assert.That(log.Track(id), Is.Empty);
        }

        [Test]
        public void CutOffLastLine_IsIgnored()
        {
            var pieces = new List<TrackPiece>();
            JobLog.ParseTrack("#1700000000000\n45.1,16.1\n45.2,16.2\n45.3", pieces); // power lost mid-line, no "=end"
            Assert.That(pieces.Count, Is.EqualTo(1));
            Assert.That(pieces[0].Points.Count, Is.EqualTo(2));
        }

        private List<JobTrack> OneJob()
        {
            JobLog log = NewLog();
            double n = Drive(log, 0, 20);
            n = Drive(log, n, 5, working: false);
            Drive(log, n, 20);
            Job job = log.Current;
            return new List<JobTrack> { new JobTrack { Job = job, Pieces = log.Track(job.Id) } };
        }

        [Test]
        public void Kml_IsValidXml_WithOneLinePerPiece()
        {
            List<JobTrack> jobs = OneJob();
            string kml = TrackExport.ToKml(jobs);
            XDocument doc = XDocument.Parse(kml); // also proves the field name was escaped
            XNamespace k = "http://www.opengis.net/kml/2.2";
            List<XElement> lines = doc.Descendants(k + "LineString").ToList();
            Assert.That(lines.Count, Is.EqualTo(2));
            string first = lines[0].Element(k + "coordinates").Value.Trim().Split('\n')[0].Trim();
            Assert.That(first, Does.StartWith("15.98190"), "longitude first, as KML wants");
            Assert.That(doc.Descendants(k + "Folder").Single().Element(k + "name").Value, Does.Contain("Gornja"));
        }

        [Test]
        public void GeoJson_HasCoordinates_AndStartAndEndPerPiece()
        {
            List<JobTrack> jobs = OneJob();
            using JsonDocument doc = JsonDocument.Parse(TrackExport.ToGeoJson(jobs));
            JsonElement f = doc.RootElement.GetProperty("features")[0];
            JsonElement coords = f.GetProperty("geometry").GetProperty("coordinates");
            JsonElement pieces = f.GetProperty("properties").GetProperty("pieces");
            Assert.That(f.GetProperty("geometry").GetProperty("type").GetString(), Is.EqualTo("MultiLineString"));
            Assert.That(coords.GetArrayLength(), Is.EqualTo(2));
            Assert.That(pieces.GetArrayLength(), Is.EqualTo(2), "a start and end time per line, not per point");
            Assert.That(DateTime.Parse(pieces[1].GetProperty("start").GetString()), Is.GreaterThan(DateTime.Parse(pieces[0].GetProperty("end").GetString())));
            Assert.That(coords[0][0][1].GetDouble(), Is.EqualTo(45.815).Within(1e-6), "[lon, lat]");
            Assert.That(f.GetProperty("properties").GetProperty("field").GetString(), Is.EqualTo(_field.Name));
        }

        [Test]
        public void JobsWithoutTrack_AreLeftOut()
        {
            var jobs = new List<JobTrack> { new JobTrack { Job = new Job { FieldName = "x" } } };
            Assert.That(XDocument.Parse(TrackExport.ToKml(jobs)).Descendants().Any(e => e.Name.LocalName == "Folder"), Is.False);
            using JsonDocument doc = JsonDocument.Parse(TrackExport.ToGeoJson(jobs));
            Assert.That(doc.RootElement.GetProperty("features").GetArrayLength(), Is.EqualTo(0));
        }
    
        [Test]
        public void SetDetails_SavesAndReloads_AndFeedsTheSuggestions()
        {
            JobLog log = NewLog();
            Drive(log, 0, 20);
            string id = log.Current.Id;
            Assert.That(log.SetDetails(id, "  Sowing wheat ", "220 kg/ha; wet \"north\" corner"), Is.True);
            Assert.That(log.SetDetails("nope", "x", "y"), Is.False);
            log.EndJob();

            JobLog again = NewLog();
            Job j = again.Jobs.Single();
            Assert.That(j.WorkType, Is.EqualTo("Sowing wheat"));
            Assert.That(j.Note, Is.EqualTo("220 kg/ha; wet \"north\" corner"));

            string csv = JobLog.ToCsv(again.Jobs);
            Assert.That(csv.Split("\r\n")[0], Does.EndWith(";Work type;Note;Implement"));
            Assert.That(csv, Does.Contain(";Sowing wheat;\"220 kg/ha; wet \"\"north\"\" corner\""), "quoted for Excel");

            var tracks = new List<JobTrack> { new JobTrack { Job = j, Pieces = again.Track(j.Id) } };
            Assert.That(XDocument.Parse(TrackExport.ToKml(tracks)).ToString(), Does.Contain("Sowing wheat"));
            using JsonDocument doc = JsonDocument.Parse(TrackExport.ToGeoJson(tracks));
            JsonElement props = doc.RootElement.GetProperty("features")[0].GetProperty("properties");
            Assert.That(props.GetProperty("workType").GetString(), Is.EqualTo("Sowing wheat"));
            Assert.That(props.GetProperty("note").GetString(), Does.Contain("north"));
        }

        [Test]
        public void UsedWorkTypes_AreTheOwnOnes_NewestFirst_NoRepeats()
        {
            JobLog log = NewLog();
            foreach (string w in new[] { "Rolling", "Sowing", "rolling", "Stone picking" })
            {
                Drive(log, 0, 5);
                log.SetDetails(log.Current.Id, w, "");
                log.EndJob();
                _t = _t.AddMinutes(10);
            }
            Assert.That(log.UsedWorkTypes(JobLog.DefaultWorkTypes), Is.EqualTo(new[] { "Stone picking", "rolling" }));
        }
}
}
