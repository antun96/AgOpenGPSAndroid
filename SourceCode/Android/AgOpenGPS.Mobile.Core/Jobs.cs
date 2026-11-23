using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>One job: a stretch of work in one field on one day.</summary>
    public sealed class Job
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FieldId { get; set; }
        public string FieldName { get; set; } = "";
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        /// <summary>Seconds with Work on.</summary>
        public double WorkingSeconds { get; set; }
        /// <summary>Ground newly worked during the job (counted once), square metres.</summary>
        public double WorkedSquareMeters { get; set; }
        /// <summary>Distance driven with Work on, metres.</summary>
        public double WorkingMeters { get; set; }
        public double ImplementWidth { get; set; }

        [JsonIgnore] public double Hectares => WorkedSquareMeters / 10000.0;
        /// <summary>Average speed while working, km/h; NaN before any working time.</summary>
        [JsonIgnore] public double AverageSpeedKmh => WorkingSeconds > 1 ? WorkingMeters / WorkingSeconds * 3.6 : double.NaN;
    }

    /// <summary>One recorded point of a job's track.</summary>
    public readonly struct TrackSample
    {
        public TrackSample(DateTime utc, double latitude, double longitude)
        {
            Utc = utc;
            Latitude = latitude;
            Longitude = longitude;
        }

        public DateTime Utc { get; }
        public double Latitude { get; }
        public double Longitude { get; }
    }

    /// <summary>
    /// Keeps the job log. A job starts when Work is switched on in a field and grows while Work is
    /// on; it ends when the field changes, a new day starts, or <see cref="EndJob"/> is called.
    /// Sampled a few times a second (<see cref="Sample"/>). Saved as JSON. Thread safe.
    /// <para>
    /// Each job also keeps its track: where the implement went while Work was on, a point about
    /// every <see cref="TrackSpacingMeters"/>, in a small text file per job ("time,lat,lon" lines;
    /// "#" starts a new piece after Work was off or the signal was lost).
    /// </para>
    /// </summary>
    public sealed class JobLog
    {
        /// <summary>Gaps longer than this (no samples, signal lost) do not count as working time.</summary>
        public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);
        public const double MaxStepMeters = 10.0;
        /// <summary>A track point is kept once the implement moved this far from the last one.</summary>
        public const double TrackSpacingMeters = 1.0;
        /// <summary>Keep the last point at least this often, even standing still (shows stops).</summary>
        public static readonly TimeSpan TrackMaxInterval = TimeSpan.FromSeconds(10);

        private readonly string _path;
        private readonly object _lock = new object();
        private JobFile _data = new JobFile();
        private Job _current;
        private DateTime? _lastSample;
        private LocalPlane _plane;
        private GeoPoint? _lastPos;
        private double _lastCovered = double.NaN;
        private bool _dirty;

        private readonly string _trackDir;
        private readonly StringBuilder _trackBuffer = new StringBuilder();
        private string _trackBufferJob;
        private GeoPoint? _lastTrackPos;
        private DateTime _lastTrackUtc;
        private bool _trackBreak = true;

        /// <param name="trackDirectory">Where the tracks go; null = "tracks" next to the log; "" = no tracks.</param>
        public JobLog(string path, string trackDirectory = null)
        {
            _path = path;
            _trackDir = trackDirectory ?? (path == null ? "" : Path.Combine(Path.GetDirectoryName(path) ?? "", "tracks"));
            Load();
        }

        private string TrackPath(string jobId) => string.IsNullOrEmpty(_trackDir) ? null : Path.Combine(_trackDir, jobId + ".trk");

        /// <summary>The job's track in pieces (a new piece after each pause or gap), oldest first. Empty without one.</summary>
        public List<List<TrackSample>> Track(string jobId)
        {
            var pieces = new List<List<TrackSample>>();
            lock (_lock)
            {
                string text = "";
                string file = TrackPath(jobId);
                try { if (file != null && File.Exists(file)) text = File.ReadAllText(file); }
                catch (IOException) { }
                if (_trackBufferJob == jobId) text += _trackBuffer.ToString();
                ParseTrack(text, pieces);
            }
            return pieces;
        }

        internal static void ParseTrack(string text, List<List<TrackSample>> pieces)
        {
            List<TrackSample> piece = null;
            var ci = CultureInfo.InvariantCulture;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#') { piece = null; continue; }
                string[] f = line.Split(',');
                if (f.Length < 3 || !long.TryParse(f[0], NumberStyles.Integer, ci, out long ms)
                    || !double.TryParse(f[1], NumberStyles.Float, ci, out double lat)
                    || !double.TryParse(f[2], NumberStyles.Float, ci, out double lon)) continue; // a cut-off last line
                if (piece == null) pieces.Add(piece = new List<TrackSample>());
                piece.Add(new TrackSample(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, lat, lon));
            }
            pieces.RemoveAll(p => p.Count < 2);
        }

        private void AddTrackPoint(DateTime utc, double latitude, double longitude, GeoPoint here)
        {
            if (string.IsNullOrEmpty(_trackDir)) return;
            if (_trackBufferJob != _current.Id)
            {
                FlushTrackLocked();
                _trackBufferJob = _current.Id;
            }
            bool far = !_lastTrackPos.HasValue || _lastTrackPos.Value.DistanceTo(here) >= TrackSpacingMeters;
            if (!_trackBreak && !far && utc - _lastTrackUtc < TrackMaxInterval) return;
            if (_trackBreak) _trackBuffer.Append("#\n");
            var ci = CultureInfo.InvariantCulture;
            _trackBuffer.Append(new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(ci)).Append(',')
                .Append(latitude.ToString("0.0000000", ci)).Append(',')
                .Append(longitude.ToString("0.0000000", ci)).Append('\n');
            _trackBreak = false;
            _lastTrackPos = here;
            _lastTrackUtc = utc;
        }

        private void FlushTrackLocked()
        {
            if (_trackBuffer.Length == 0 || _trackBufferJob == null) return;
            try
            {
                Directory.CreateDirectory(_trackDir);
                File.AppendAllText(TrackPath(_trackBufferJob), _trackBuffer.ToString());
                _trackBuffer.Clear();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Keep it in memory and try again with the next save.
            }
        }

        public IReadOnlyList<Job> Jobs { get { lock (_lock) return _data.Jobs.OrderByDescending(j => j.StartUtc).ToList(); } }
        public Job Current { get { lock (_lock) return _current; } }

        /// <summary>
        /// One sample: whether Work is on, the field, the implement's position and the total worked
        /// area of the field's coverage map (only its growth counts for the job).
        /// </summary>
        public void Sample(DateTime nowUtc, bool working, Field field, double latitude, double longitude,
            double coveredSquareMeters, double implementWidth)
        {
            lock (_lock)
            {
                // A different field or a new (local) day ends the job.
                if (_current != null && (field == null || field.Id != _current.FieldId || nowUtc.ToLocalTime().Date != _current.StartUtc.ToLocalTime().Date))
                    EndLocked();

                if (!working || field == null || double.IsNaN(latitude))
                {
                    _trackBreak = true;
                    _lastSample = null;
                    _lastPos = null;
                    _lastCovered = coveredSquareMeters;
                    return;
                }

                if (_current == null)
                {
                    _current = new Job { FieldId = field.Id, FieldName = field.Name, StartUtc = nowUtc, EndUtc = nowUtc, ImplementWidth = implementWidth };
                    _data.Jobs.Add(_current);
                    _plane = new LocalPlane(latitude, longitude);
                    _lastSample = null;
                    _lastPos = null;
                    _lastCovered = coveredSquareMeters;
                    _lastTrackPos = null;
                    _trackBreak = true;
                }

                GeoPoint here = _plane.ToLocal(latitude, longitude);
                if (_lastSample.HasValue)
                {
                    TimeSpan dt = nowUtc - _lastSample.Value;
                    if (dt > TimeSpan.Zero && dt <= MaxSampleGap) _current.WorkingSeconds += dt.TotalSeconds;
                    else _trackBreak = true; // signal lost for a while: do not join across the gap
                }
                if (_lastPos.HasValue)
                {
                    double step = _lastPos.Value.DistanceTo(here);
                    if (step <= MaxStepMeters) _current.WorkingMeters += step;
                    else _trackBreak = true;
                }
                AddTrackPoint(nowUtc, latitude, longitude, here);
                if (!double.IsNaN(_lastCovered) && !double.IsNaN(coveredSquareMeters) && coveredSquareMeters > _lastCovered)
                    _current.WorkedSquareMeters += coveredSquareMeters - _lastCovered;

                _lastCovered = coveredSquareMeters;
                _lastSample = nowUtc;
                _lastPos = here;
                _current.EndUtc = nowUtc;
                _current.ImplementWidth = implementWidth;
                _dirty = true;
            }
        }

        /// <summary>Closes the running job (if any) and saves.</summary>
        public void EndJob()
        {
            lock (_lock) EndLocked();
        }

        private void EndLocked()
        {
            if (_current != null && _current.WorkingSeconds < 1 && _current.WorkedSquareMeters < 1)
            {
                _data.Jobs.Remove(_current); // switched on and off again at once: no job
                DeleteTrackLocked(_current.Id);
            }
            else FlushTrackLocked();
            _current = null;
            _lastSample = null;
            _lastPos = null;
            _lastTrackPos = null;
            _trackBreak = true;
            _dirty = true;
            SaveLocked();
        }

        public void Delete(string id)
        {
            lock (_lock)
            {
                if (_current?.Id == id) _current = null;
                _data.Jobs.RemoveAll(j => j.Id == id);
                DeleteTrackLocked(id);
                _dirty = true;
                SaveLocked();
            }
        }

        private void DeleteTrackLocked(string id)
        {
            if (_trackBufferJob == id)
            {
                _trackBuffer.Clear();
                _trackBufferJob = null;
            }
            string file = TrackPath(id);
            try { if (file != null) File.Delete(file); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Saves if something changed (call every few seconds).</summary>
        public void Save()
        {
            lock (_lock) SaveLocked();
        }

        private void SaveLocked()
        {
            FlushTrackLocked();
            if (!_dirty || _path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JobJson.Default.JobFile));
                File.Move(tmp, _path, true);
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Never let saving the log stop the app; try again next time.
            }
        }

        private void Load()
        {
            try
            {
                if (_path != null && File.Exists(_path))
                    _data = JsonSerializer.Deserialize(File.ReadAllText(_path), JobJson.Default.JobFile) ?? new JobFile();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                _data = new JobFile();
            }
        }

        /// <summary>The jobs as CSV (semicolon separated, opens in Excel with European settings), oldest first.</summary>
        public static string ToCsv(IEnumerable<Job> jobs, Func<string, string> t = null)
        {
            t ??= s => s;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(string.Join(";", new[] { "Date", "Field", "Start", "End", "Working time (h:mm)", "Worked (ha)",
                "Distance (km)", "Average speed (km/h)", "Implement width (m)" }.Select(t))).Append("\r\n");
            foreach (Job j in jobs.OrderBy(j => j.StartUtc))
            {
                DateTime s = j.StartUtc.ToLocalTime(), e = j.EndUtc.ToLocalTime();
                sb.Append(s.ToString("yyyy-MM-dd", ci)).Append(';')
                  .Append(Csv(j.FieldName)).Append(';')
                  .Append(s.ToString("HH:mm", ci)).Append(';')
                  .Append(e.ToString("HH:mm", ci)).Append(';')
                  .Append(Duration(j.WorkingSeconds)).Append(';')
                  .Append(j.Hectares.ToString("0.00", ci)).Append(';')
                  .Append((j.WorkingMeters / 1000).ToString("0.00", ci)).Append(';')
                  .Append(double.IsNaN(j.AverageSpeedKmh) ? "" : j.AverageSpeedKmh.ToString("0.0", ci)).Append(';')
                  .Append(j.ImplementWidth.ToString("0.00", ci)).Append("\r\n");
            }
            return sb.ToString();
        }

        public static string Duration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)ts.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + ts.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Csv(string s)
        {
            s ??= "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        internal sealed class JobFile
        {
            public int Version { get; set; } = 1;
            public List<Job> Jobs { get; set; } = new List<Job>();
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(JobLog.JobFile))]
    internal partial class JobJson : JsonSerializerContext
    {
    }
}
