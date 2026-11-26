using System;
using System.IO;
using System.Linq;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;
using NUnit.Framework;

namespace AgOpenGPS.Mobile.Core.Tests
{
    public class ImplementTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aog-impl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Path_ => Path.Combine(_dir, "implements.json");

        [Test]
        public void SaveSelectDelete_AndKeptAfterRestart()
        {
            var store = new ImplementStore(Path_);
            Assert.That(store.Active, Is.Null);
            Implement planter = store.Save(new Implement { Name = " Planter 6 rows ", Width = 4.5, Overlap = 0.05, Behind = 1.8, WorkType = "Sowing" });
            Implement sprayer = store.Save(new Implement { Name = "Sprayer", Width = 15, HeadlandWidth = 30 });
            Assert.That(store.Active.Id, Is.EqualTo(planter.Id), "the first one saved is used");
            store.SetActive(sprayer.Id);

            var again = new ImplementStore(Path_);
            Assert.That(again.Implements.Select(i => i.Name), Is.EqualTo(new[] { "Planter 6 rows", "Sprayer" }));
            Assert.That(again.Active.Name, Is.EqualTo("Sprayer"));
            Assert.That(again.Active.HeadlandWidth, Is.EqualTo(30));
            Implement p = again.Implements.First();
            Assert.That(p.PassSpacing, Is.EqualTo(4.45).Within(1e-9));
            Assert.That(p.WorkType, Is.EqualTo("Sowing"));

            again.Delete(sprayer.Id);
            Assert.That(again.Active.Name, Is.EqualTo("Planter 6 rows"), "deleting the one in use picks another");
        }

        [Test]
        public void Save_ReplacesById_AndKeepsValuesSensible()
        {
            var store = new ImplementStore(Path_);
            Implement a = store.Save(new Implement { Name = "", Width = 0, Overlap = 5 });
            Assert.That(a.Name, Is.EqualTo("Implement 1"));
            Assert.That(a.Width, Is.EqualTo(0.1));
            Assert.That(a.Overlap, Is.EqualTo(0), "overlap cannot reach the width");
            a.Name = "Harrow";
            a.Width = 3;
            store.Save(a);
            Assert.That(store.Implements.Single().Name, Is.EqualTo("Harrow"));
            Assert.That(store.NextName(), Is.EqualTo("Implement 1"));
        }

        [Test]
        public void Job_RecordsTheImplement_AndStartsWithItsWorkType()
        {
            var log = new JobLog(Path.Combine(_dir, "jobs.json"));
            var field = new Field { Name = "North" };
            var plane = new LocalPlane(45.8, 16.0);
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 20; i++)
            {
                plane.ToWgs84(new GeoPoint(i * 0.7, 0), out double lat, out double lon);
                log.Sample(t.AddSeconds(i * 0.25), true, field, lat, lon, i * 4, 4.5, "Planter 6 rows", "Sowing");
            }
            Job j = log.Current;
            Assert.That(j.ImplementName, Is.EqualTo("Planter 6 rows"));
            Assert.That(j.WorkType, Is.EqualTo("Sowing"));
            Assert.That(JobLog.ToCsv(new[] { j }), Does.Contain(";Sowing;;Planter 6 rows"));
        }

        [Test]
        public void Backup_CarriesTheImplements() => Assert.That(Backup.IsDataFile("implements.json"), Is.True);
    }
}
