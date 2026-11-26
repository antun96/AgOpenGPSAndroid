using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>
    /// A saved implement: its name and the settings that change with it (width, overlap, where it
    /// works behind and beside the tractor, optionally its own headland width and work type).
    /// </summary>
    public sealed class Implement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        /// <summary>Full working width, metres.</summary>
        public double Width { get; set; } = 6.0;
        /// <summary>Overlap between passes, metres (passes are width minus overlap apart).</summary>
        public double Overlap { get; set; }
        /// <summary>Working line behind the rear axle, metres (front mounted = negative).</summary>
        public double Behind { get; set; }
        /// <summary>Implement centre right of the tractor centre line, metres (left = negative).</summary>
        public double Right { get; set; }
        /// <summary>Headland width to use with this implement, metres; null = leave the headland setting as it is.</summary>
        public double? HeadlandWidth { get; set; }
        /// <summary>Work type a new job gets with this implement (e.g. "Sowing"); empty = ask.</summary>
        public string WorkType { get; set; } = "";

        public double PassSpacing => Math.Max(0.1, Width - Overlap);

        public Implement Clone() => (Implement)MemberwiseClone();
    }

    /// <summary>The saved implements and which one is on the tractor, in one JSON file. Thread safe.</summary>
    public sealed class ImplementStore
    {
        private readonly string _path;
        private readonly object _lock = new object();
        private ImplementFile _data = new ImplementFile();

        public ImplementStore(string path)
        {
            _path = path;
            Load();
        }

        /// <summary>All implements, by name.</summary>
        public IReadOnlyList<Implement> Implements
        {
            get { lock (_lock) return _data.Implements.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => i.Clone()).ToList(); }
        }

        /// <summary>The implement in use, or null when none is saved.</summary>
        public Implement Active
        {
            get { lock (_lock) return _data.Implements.FirstOrDefault(i => i.Id == _data.ActiveId)?.Clone(); }
        }

        /// <summary>Adds a new implement, or replaces the one with the same id. Returns the stored copy.</summary>
        public Implement Save(Implement implement)
        {
            if (implement == null) throw new ArgumentNullException(nameof(implement));
            lock (_lock)
            {
                Implement copy = implement.Clone();
                copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? NextNameLocked() : copy.Name.Trim();
                copy.Width = Math.Max(0.1, Math.Min(100, copy.Width));
                copy.Overlap = Math.Max(0, Math.Min(copy.Width - 0.1, copy.Overlap));
                copy.WorkType = (copy.WorkType ?? "").Trim();
                int at = _data.Implements.FindIndex(i => i.Id == copy.Id);
                if (at >= 0) _data.Implements[at] = copy;
                else _data.Implements.Add(copy);
                _data.ActiveId ??= copy.Id;
                SaveLocked();
                return copy.Clone();
            }
        }

        public void SetActive(string id)
        {
            lock (_lock)
            {
                if (_data.Implements.Any(i => i.Id == id)) _data.ActiveId = id;
                SaveLocked();
            }
        }

        /// <summary>Deletes an implement; if it was in use, the first remaining one is used.</summary>
        public void Delete(string id)
        {
            lock (_lock)
            {
                _data.Implements.RemoveAll(i => i.Id == id);
                if (_data.ActiveId == id) _data.ActiveId = _data.Implements.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault()?.Id;
                SaveLocked();
            }
        }

        public string NextName()
        {
            lock (_lock) return NextNameLocked();
        }

        private string NextNameLocked()
        {
            for (int n = 1; ; n++)
            {
                string name = "Implement " + n;
                if (!_data.Implements.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
            }
        }

        private void Load()
        {
            try
            {
                if (_path == null || !File.Exists(_path)) return;
                ImplementFile data = JsonSerializer.Deserialize(File.ReadAllText(_path), ImplementJson.Default.ImplementFile);
                if (data?.Implements != null) _data = data;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _data = new ImplementFile();
            }
        }

        private void SaveLocked()
        {
            if (_path == null) return;
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data, ImplementJson.Default.ImplementFile));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Storage full or gone: the change stays in memory and is saved with the next change.
            }
        }

        internal sealed class ImplementFile
        {
            public int Version { get; set; } = 1;
            public string ActiveId { get; set; }
            public List<Implement> Implements { get; set; } = new List<Implement>();
        }
    }

    // Source-generated serializer: works with Android's trimming, no reflection.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ImplementStore.ImplementFile))]
    internal partial class ImplementJson : JsonSerializerContext
    {
    }
}
