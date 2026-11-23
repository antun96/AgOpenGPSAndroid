using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class RecordsTests
    {
        private static readonly LocalPlane Plane = new LocalPlane(45.8150, 15.9819);
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aog-rec-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static Job J(string fieldId, string field, string type, int month, int day, double ha, double hours) => new Job
        {
            FieldId = fieldId, FieldName = field, WorkType = type,
            StartUtc = new DateTime(2026, month, day, 9, 0, 0, DateTimeKind.Local).ToUniversalTime(),
            EndUtc = new DateTime(2026, month, day, 9, 0, 0, DateTimeKind.Local).ToUniversalTime().AddHours(hours),
            WorkedSquareMeters = ha * 10000, WorkingSeconds = hours * 3600, WorkingMeters = ha * 1000,
        };

        private static Field Square(string name, double size, string crop = "")
        {
            var f = new Field { Name = name, Crop = crop };
            foreach (var (n, e) in new[] { (0.0, 0.0), (0.0, size), (size, size), (size, 0.0) })
            {
                Plane.ToWgs84(new GeoPoint(n, e), out double lat, out double lon);
                f.Boundary.Add(new TrackPoint(lat, lon));
            }
            return f;
        }

        [Test]
        public void Filter_ByFieldDateAndWorkType()
        {
            var jobs = new List<Job>
            {
                J("a", "North", "Sowing", 4, 2, 5, 2), J("a", "North", "Spraying", 5, 10, 5, 1),
                J("b", "South", "Sowing", 4, 3, 3, 1.5), J("b", "South", "", 6, 1, 1, 0.5),
            };
            Assert.That(new JobFilter { FieldId = "a" }.Apply(jobs).Count, Is.EqualTo(2));
            Assert.That(new JobFilter { WorkType = "sowing" }.Apply(jobs).Count, Is.EqualTo(2), "ignoring case");
            Assert.That(new JobFilter { WorkType = "" }.Apply(jobs).Single().FieldName, Is.EqualTo("South"));
            Assert.That(new JobFilter { FromLocal = new DateTime(2026, 4, 3), ToLocal = new DateTime(2026, 5, 10) }.Apply(jobs).Count, Is.EqualTo(2));
            List<Job> newestFirst = new JobFilter().Apply(jobs);
            Assert.That(newestFirst[0].StartUtc, Is.GreaterThan(newestFirst[1].StartUtc));

            JobTotals t = JobTotals.Of(new JobFilter { WorkType = "Sowing" }.Apply(jobs));
            Assert.That(t.Count, Is.EqualTo(2));
            Assert.That(t.Hectares, Is.EqualTo(8).Within(1e-9));
            Assert.That(t.Hours, Is.EqualTo(3.5).Within(1e-9));
        }

        [Test]
        public void Season_ListsEveryFieldAgainstEveryWorkType()
        {
            var fields = new List<Field> { Square("North", 100, "Wheat"), Square("Empty", 50) };
            fields[0].Id = "a";
            var jobs = new List<Job>
            {
                J("a", "North", "Sowing", 4, 2, 0.6, 2), J("a", "North", "Sowing", 4, 3, 0.4, 1),
                J("a", "North", "Spraying", 5, 10, 1, 1),
                J("gone", "Old field", "Mowing", 6, 1, 2, 1),     // field deleted since
                J("a", "North", "Sowing", 4, 2, 9, 9).Also(j => j.StartUtc = j.StartUtc.AddYears(-1)), // last year
            };
            SeasonSummary s = SeasonSummary.Build(2026, fields, jobs);
            Assert.That(s.WorkTypes, Is.EqualTo(new[] { "Sowing", "Spraying", "Mowing" }));
            SeasonRow north = s.Rows.Single(r => r.FieldName == "North");
            Assert.That(north.Crop, Is.EqualTo("Wheat"));
            Assert.That(north.AreaHectares, Is.EqualTo(1).Within(0.001));
            Assert.That(north.Cells["Sowing"].Hectares, Is.EqualTo(1).Within(1e-9));
            Assert.That(north.Cells["Sowing"].Jobs, Is.EqualTo(2));
            Assert.That(north.Cells["Sowing"].LastLocal, Is.EqualTo(new DateTime(2026, 4, 3)));
            Assert.That(north.Cells.ContainsKey("Mowing"), Is.False);
            Assert.That(s.Rows.Single(r => r.FieldName == "Empty").Cells, Is.Empty, "fields with nothing done are listed too");
            Assert.That(s.Rows.Single(r => r.FieldName == "Old field").Cells["Mowing"].Hectares, Is.EqualTo(2));
            Assert.That(s.Total.Count, Is.EqualTo(4));

            string csv = s.ToCsv();
            string[] lines = csv.Split("\r\n");
            Assert.That(lines[0], Is.EqualTo("Field;Crop;Area (ha);Sowing (ha);Spraying (ha);Mowing (ha);Total (ha);Working time (h:mm)"));
            Assert.That(lines.Any(l => l.StartsWith("North;Wheat;1.00;1.00;1.00;;2.00;4:00")), Is.True, csv);
        }

        [Test]
        public void InputRecord_TotalsFromDoseAndArea()
        {
            Job spray = J("a", "North", "Spraying", 5, 10, 4.5, 1);
            spray.Crop = "Wheat";
            spray.WaterLitersPerHa = 200;
            spray.Inputs = new List<JobInput>
            {
                new JobInput { Product = "Herbicide X", Dose = 1.2, Unit = "l/ha", Target = "broadleaf weeds; thistle" },
                new JobInput { Product = "Adjuvant", Dose = 150, Unit = "ml/ha" },
            };
            Job fert = J("b", "South", "Fertilising", 3, 20, 2, 1);
            fert.Inputs = new List<JobInput> { new JobInput { Product = "KAN 27%", Dose = 200, Unit = "kg/ha" } };
            Job again = J("a", "North", "Spraying", 6, 1, 1, 1);
            again.Inputs = new List<JobInput> { new JobInput { Product = "herbicide x", Dose = 1, Unit = "l/ha" } };

            List<InputRecordRow> rows = InputRecord.Rows(new[] { spray, fert, again, J("c", "x", "Sowing", 4, 1, 1, 1) });
            Assert.That(rows.Count, Is.EqualTo(4), "jobs without products are not in the record");
            Assert.That(rows[0].Job, Is.SameAs(fert), "oldest first");
            Assert.That(rows[0].Total, Is.EqualTo(400).Within(1e-9));
            Assert.That(rows[0].Input.TotalUnit, Is.EqualTo("kg"));

            List<ProductTotal> totals = InputRecord.Totals(rows);
            ProductTotal herb = totals.Single(t => t.Product.Equals("Herbicide X", StringComparison.OrdinalIgnoreCase));
            Assert.That(herb.Amount, Is.EqualTo(1.2 * 4.5 + 1).Within(1e-9));
            Assert.That(herb.Uses, Is.EqualTo(2));
            Assert.That(totals.Single(t => t.Product == "Adjuvant").Unit, Is.EqualTo("ml"));

            string csv = InputRecord.ToCsv(rows);
            Assert.That(csv, Does.Contain(";North;Wheat;Spraying;4.50;Herbicide X;1.2;l/ha;5.4;l;200;\"broadleaf weeds; thistle\";"));
        }

        [Test]
        public void Inputs_AreSavedWithTheJob_AndSuggested()
        {
            string path = Path.Combine(_dir, "jobs.json");
            var log = new JobLog(path);
            var field = new Field { Name = "North", Crop = "Barley" };
            Plane.ToWgs84(new GeoPoint(0, 0), out double lat, out double lon);
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 20; i++) log.Sample(t.AddSeconds(i * 0.25), true, field, lat + i * 1e-5, lon, i * 10, 6);
            string id = log.Current.Id;
            Assert.That(log.Current.Crop, Is.EqualTo("Barley"), "the crop when the job started");
            Assert.That(log.SetInputs(id, new[]
            {
                new JobInput { Product = " Fungicide Y ", Dose = 0.8, Unit = "l/ha", Target = "rust" },
                new JobInput { Product = "  ", Dose = 5 }, // empty rows are dropped
            }, 250), Is.True);
            log.EndJob();

            Job j = new JobLog(path).Jobs.Single();
            Assert.That(j.Inputs.Single().Product, Is.EqualTo("Fungicide Y"));
            Assert.That(j.Inputs.Single().Dose, Is.EqualTo(0.8));
            Assert.That(j.WaterLitersPerHa, Is.EqualTo(250));
            Assert.That(new JobLog(path).UsedProducts(), Is.EqualTo(new[] { "Fungicide Y" }));
        }

        [Test]
        public void Field_CropVarietyNote_AreSaved()
        {
            string path = Path.Combine(_dir, "fields.json");
            var store = new FieldStore(path);
            Field f = store.Create("North");
            store.SetDetails(f.Id, " Winter wheat ", "Kraljica", "Wet in the north corner");
            Field back = new FieldStore(path).Fields.Single();
            Assert.That(back.Crop, Is.EqualTo("Winter wheat"));
            Assert.That(back.Variety, Is.EqualTo("Kraljica"));
            Assert.That(back.Note, Is.EqualTo("Wet in the north corner"));
        }

        [Test]
        public void FullBackup_RoundTripsTheDataFiles_AndRefusesOtherPaths()
        {
            var files = new Dictionary<string, byte[]>
            {
                ["fields.json"] = Encoding.UTF8.GetBytes("{\"x\":1}"),
                ["jobs.json"] = Encoding.UTF8.GetBytes("{}"),
                ["tracks/0a1b2c.trk"] = Encoding.UTF8.GetBytes("#1\n45,16\n=2\n"),
                ["coverage/0a1b2c.bin"] = new byte[] { 0, 1, 2, 255 },
                ["../evil.sh"] = new byte[] { 1 },
                ["maptiles/1/2/3.png"] = new byte[] { 1 },
            };
            var settings = new Dictionary<string, object> { ["tool.width"] = "6.0", ["ntrip.enabled"] = true };
            string text = Backup.WriteFull(settings, files, DateTime.UtcNow);
            Assert.That(text, Does.Not.Contain("evil"));
            Assert.That(text, Does.Not.Contain("maptiles"));

            Backup.Read(text, out Dictionary<string, object> s, out string fieldsJson, out Dictionary<string, byte[]> back);
            Assert.That(s["tool.width"], Is.EqualTo("6.0"));
            Assert.That(s["ntrip.enabled"], Is.EqualTo(true));
            Assert.That(back.Keys, Is.EquivalentTo(new[] { "fields.json", "jobs.json", "tracks/0a1b2c.trk", "coverage/0a1b2c.bin" }));
            Assert.That(back["coverage/0a1b2c.bin"], Is.EqualTo(new byte[] { 0, 1, 2, 255 }));

            // A hand-edited file with a path outside the data folder is not written on restore.
            string edited = text.Replace("\"jobs.json\"", "\"../../jobs.json\"");
            Backup.Read(edited, out _, out _, out Dictionary<string, byte[]> safe);
            Assert.That(safe.ContainsKey("../../jobs.json"), Is.False);
        }

        [TestCase("fields.json", true)]
        [TestCase("tracks/abc123.trk", true)]
        [TestCase("coverage/abc-123.bin", true)]
        [TestCase("tracks/../x.trk", false)]
        [TestCase("tracks/a/b.trk", false)]
        [TestCase("coverage/abc.trk", false)]
        [TestCase("settings.xml", false)]
        [TestCase("/data/fields.json", false)]
        public void IsDataFile(string path, bool ok) => Assert.That(Backup.IsDataFile(path), Is.EqualTo(ok));

        [Test]
        public void OldBackup_HasNoFiles()
        {
            string v1 = Backup.Write(new Dictionary<string, object> { ["a"] = "b" }, null, DateTime.UtcNow);
            Backup.Read(v1, out _, out _, out Dictionary<string, byte[]> files);
            Assert.That(files, Is.Null);
        }
    }

    internal static class TestExtensions
    {
        public static T Also<T>(this T x, Action<T> a)
        {
            a(x);
            return x;
        }
    }
}
