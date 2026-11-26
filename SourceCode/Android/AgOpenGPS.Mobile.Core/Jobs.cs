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
        /// <summary>Name of the saved implement used (the last one, if it was changed during the job).</summary>
        public string ImplementName { get; set; } = "";
        /// <summary>What was done, e.g. "Sowing" (in the words the driver chose); empty if not given.</summary>
        public string WorkType { get; set; } = "";
        /// <summary>The driver's own note, e.g. "Wheat, 220 kg/ha, wet in the north corner".</summary>
        public string Note { get; set; } = "";
        /// <summary>The field's crop when the job started (the field may be sown with something else later).</summary>
        public string Crop { get; set; } = "";
        /// <summary>Products applied: sprays, fertilisers, seed, each with its dose per hectare.</summary>
        public List<JobInput> Inputs { get; set; } = new List<JobInput>();
        /// <summary>Spray water volume, litres per hectare; null if not given.</summary>
        public double? WaterLitersPerHa { get; set; }

        [JsonIgnore] public double Hectares => WorkedSquareMeters / 10000.0;
        /// <summary>Average speed while working, km/h; NaN before any working time.</summary>
        [JsonIgnore] public double AverageSpeedKmh => WorkingSeconds > 1 ? WorkingMeters / WorkingSeconds * 3.6 : double.NaN;
    }

    /// <summary>One product applied in a job: a spray, a fertiliser, seed...</summary>
    public sealed class JobInput
    {
        /// <summary>Product name as on the label, e.g. "Roundup Max".</summary>
        public string Product { get; set; } = "";
        /// <summary>Amount per hectare, in <see cref="Unit"/>.</summary>
        public double Dose { get; set; }
        /// <summary>"l/ha", "kg/ha", "ml/ha", "g/ha", "t/ha" or "seeds/ha".</summary>
        public string Unit { get; set; } = "l/ha";
        /// <summary>What it was used against (weed, pest, disease) or what for; may be empty.</summary>
        public string Target { get; set; } = "";

        public static readonly string[] Units = { "l/ha", "kg/ha", "ml/ha", "g/ha", "t/ha", "seeds/ha" };

        /// <summary>The unit of the total: "l/ha" gives "l".</summary>
        public string TotalUnit => Unit != null && Unit.EndsWith("/ha", StringComparison.Ordinal) ? Unit.Substring(0, Unit.Length - 3) : Unit ?? "";

        /// <summary>Total amount for <paramref name="hectares"/>.</summary>
        public double Total(double hectares) => Dose * Math.Max(0, hectares);
    }

    /// <summary>
    /// One stretch of a job's track, driven without a pause: its positions (about one per metre)
    /// and only its start and end time.
    /// </summary>
    public sealed class TrackPiece
    {
        public DateTime StartUtc;
        public DateTime EndUtc;
        public List<TrackPoint> Points = new List<TrackPoint>();
    }

    /// <summary>
    /// Keeps the job log. A job starts when Work is switched on in a field and grows while Work is
    /// on; it ends when the field changes, a new day starts, or <see cref="EndJob"/> is called.
    /// Sampled a few times a second (<see cref="Sample"/>). Saved as JSON. Thread safe.
    /// <para>
    /// Each job also keeps its track: where the implement went while Work was on, a point about
    /// every <see cref="TrackSpacingMeters"/>, in a small text file per job. A piece of track starts
    /// with "#start" and ends with "=end" (times in Unix milliseconds); between them are
    /// "lat,lon" lines. A new piece starts after Work was off or the signal was lost.
    /// </para>
    /// </summary>
    public sealed class JobLog
    {
        /// <summary>Gaps longer than this (no samples, signal lost) do not count as working time.</summary>
        public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(5);
        public const double MaxStepMeters = 10.0;
        /// <summary>A track point is kept once the implement moved this far from the last one.</summary>
        public const double TrackSpacingMeters = 1.0;

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
        private bool _trackBreak = true;
        private bool _pieceOpen;          // "#start" written, "=end" not yet
        private DateTime _pieceLastUtc;   // last moment of the open piece

        /// <param name="trackDirectory">Where the tracks go; null = "tracks" next to the log; "" = no tracks.</param>
        public JobLog(string path, string trackDirectory = null)
        {
            _path = path;
            _trackDir = trackDirectory ?? (path == null ? "" : Path.Combine(Path.GetDirectoryName(path) ?? "", "tracks"));
            Load();
        }

        private string TrackPath(string jobId) => string.IsNullOrEmpty(_trackDir) ? null : Path.Combine(_trackDir, jobId + ".trk");

        private static string Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        /// <summary>The job's track in pieces (a new piece after each pause or gap), oldest first. Empty without one.</summary>
        public List<TrackPiece> Track(string jobId)
        {
            var pieces = new List<TrackPiece>();
            lock (_lock)
            {
                string text = "";
                string file = TrackPath(jobId);
                try { if (file != null && File.Exists(file)) text = File.ReadAllText(file); }
                catch (IOException) { }
                if (_trackBufferJob == jobId)
                {
                    text += _trackBuffer.ToString();
                    if (_pieceOpen) text += "=" + Ms(_pieceLastUtc) + "\n"; // the piece still being driven
                }
                ParseTrack(text, pieces);
            }
            return pieces;
        }

        internal static void ParseTrack(string text, List<TrackPiece> pieces)
        {
            TrackPiece piece = null;
            var ci = CultureInfo.InvariantCulture;
            DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#' || line[0] == '=')
                {
                    long.TryParse(line.Substring(1), NumberStyles.Integer, ci, out long ms);
                    if (line[0] == '#')
                    {
                        piece = new TrackPiece();
                        if (ms > 0) piece.StartUtc = piece.EndUtc = FromMs(ms);
                        pieces.Add(piece);
                    }
                    else
                    {
                        if (piece != null && ms > 0) piece.EndUtc = FromMs(ms);
                        piece = null;
                    }
                    continue;
                }
                string[] f = line.Split(',');
                // "lat,lon"; tracks from version 0.14.2 had "time,lat,lon" (read as well).
                int at = f.Length >= 3 ? 1 : 0;
                if (f.Length < 2 || !double.TryParse(f[at], NumberStyles.Float, ci, out double lat)
                    || !double.TryParse(f[at + 1], NumberStyles.Float, ci, out double lon)) continue; // a cut-off last line
                if (piece == null) pieces.Add(piece = new TrackPiece());
                if (at == 1 && long.TryParse(f[0], NumberStyles.Integer, ci, out long t))
                {
                    if (piece.Points.Count == 0) piece.StartUtc = FromMs(t);
                    piece.EndUtc = FromMs(t);
                }
                piece.Points.Add(new TrackPoint(lat, lon));
            }
            pieces.RemoveAll(p => p.Points.Count < 2);
        }

        private void AddTrackPoint(DateTime utc, double latitude, double longitude, GeoPoint here)
        {
            if (string.IsNullOrEmpty(_trackDir)) return;
            if (_trackBufferJob != _current.Id)
            {
                ClosePieceLocked();
                FlushTrackLocked();
                _trackBufferJob = _current.Id;
            }
            if (_trackBreak)
            {
                ClosePieceLocked();
                _trackBuffer.Append('#').Append(Ms(utc)).Append('\n');
                _pieceOpen = true;
                _lastTrackPos = null;
                _trackBreak = false;
            }
            _pieceLastUtc = utc;
            if (_lastTrackPos.HasValue && _lastTrackPos.Value.DistanceTo(here) < TrackSpacingMeters) return;
            var ci = CultureInfo.InvariantCulture;
            _trackBuffer.Append(latitude.ToString("0.0000000", ci)).Append(',')
                .Append(longitude.ToString("0.0000000", ci)).Append('\n');
            _lastTrackPos = here;
        }

        /// <summary>Writes the end time of the piece being driven, if any.</summary>
        private void ClosePieceLocked()
        {
            if (!_pieceOpen) return;
            _trackBuffer.Append('=').Append(Ms(_pieceLastUtc)).Append('\n');
            _pieceOpen = false;
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

        /// <summary>Common work types offered first (English; translated where shown).</summary>
        public static readonly string[] DefaultWorkTypes =
            { "Ploughing", "Tilling", "Sowing", "Fertilising", "Spraying", "Mowing", "Harvesting" };

        /// <summary>Sets what was done and the note of a job. False if there is no such job.</summary>
        public bool SetDetails(string id, string workType, string note)
        {
            lock (_lock)
            {
                Job j = _data.Jobs.FirstOrDefault(x => x.Id == id);
                if (j == null) return false;
                j.WorkType = (workType ?? "").Trim();
                j.Note = (note ?? "").Trim();
                _dirty = true;
                SaveLocked();
                return true;
            }
        }

        /// <summary>Sets the products applied in a job and the spray water volume. False if there is no such job.</summary>
        public bool SetInputs(string id, IEnumerable<JobInput> inputs, double? waterLitersPerHa)
        {
            lock (_lock)
            {
                Job j = _data.Jobs.FirstOrDefault(x => x.Id == id);
                if (j == null) return false;
                j.Inputs = (inputs ?? Enumerable.Empty<JobInput>())
                    .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Product))
                    .Select(i => new JobInput { Product = i.Product.Trim(), Dose = Math.Max(0, i.Dose), Unit = i.Unit ?? "l/ha", Target = (i.Target ?? "").Trim() })
                    .ToList();
                j.WaterLitersPerHa = waterLitersPerHa.HasValue && waterLitersPerHa.Value > 0 ? waterLitersPerHa : null;
                _dirty = true;
                SaveLocked();
                return true;
            }
        }

        /// <summary>Products typed in earlier jobs, most recent first, without repeats; for suggestions.</summary>
        public List<string> UsedProducts()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            lock (_lock)
                foreach (Job j in _data.Jobs.OrderByDescending(x => x.StartUtc))
                    foreach (JobInput i in j.Inputs ?? new List<JobInput>())
                        if (!string.IsNullOrWhiteSpace(i.Product) && seen.Add(i.Product.Trim())) list.Add(i.Product.Trim());
            return list;
        }

        /// <summary>
        /// Work types typed in earlier jobs, most recently used first, without repeats (ignoring
        /// case) and without the <paramref name="known"/> ones (e.g. the default list, translated).
        /// </summary>
        public List<string> UsedWorkTypes(IEnumerable<string> known = null)
        {
            var seen = new HashSet<string>(known ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            lock (_lock)
            {
                foreach (Job j in _data.Jobs.OrderByDescending(x => x.StartUtc))
                {
                    string w = (j.WorkType ?? "").Trim();
                    if (w.Length > 0 && seen.Add(w)) list.Add(w);
                }
            }
            return list;
        }

        public IReadOnlyList<Job> Jobs { get { lock (_lock) return _data.Jobs.OrderByDescending(j => j.StartUtc).ToList(); } }
        public Job Current { get { lock (_lock) return _current; } }

        /// <summary>
        /// One sample: whether Work is on, the field, the implement's position and the total worked
        /// area of the field's coverage map (only its growth counts for the job).
        /// </summary>
        /// <param name="implementName">The saved implement in use, recorded with the job.</param>
        /// <param name="implementWorkType">The implement's usual work type: a new job starts with it.</param>
        public void Sample(DateTime nowUtc, bool working, Field field, double latitude, double longitude,
            double coveredSquareMeters, double implementWidth, string implementName = null, string implementWorkType = null)
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
                    _current = new Job
                    {
                        FieldId = field.Id, FieldName = field.Name, Crop = field.Crop ?? "", StartUtc = nowUtc, EndUtc = nowUtc,
                        ImplementWidth = implementWidth, ImplementName = implementName ?? "", WorkType = (implementWorkType ?? "").Trim(),
                    };
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
                if (!string.IsNullOrEmpty(implementName)) _current.ImplementName = implementName;
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
            else
            {
                ClosePieceLocked();
                FlushTrackLocked();
            }
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
                _pieceOpen = false;
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
                "Distance (km)", "Average speed (km/h)", "Implement width (m)", "Work type", "Note", "Implement" }.Select(t))).Append("\r\n");
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
                  .Append(j.ImplementWidth.ToString("0.00", ci)).Append(';')
                  .Append(Csv(j.WorkType)).Append(';')
                  .Append(Csv(j.Note)).Append(';')
                  .Append(Csv(j.ImplementName)).Append("\r\n");
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
