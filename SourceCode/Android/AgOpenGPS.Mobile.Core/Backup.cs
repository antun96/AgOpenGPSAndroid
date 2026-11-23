using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AgOpenGPS.Mobile.Core
{
    /// <summary>
    /// One backup file. Version 1: all settings (as typed key/value pairs) plus the fields as the
    /// GeoJSON of <see cref="Guidance.FieldExport"/>. Version 2 ("everything") adds the app's data
    /// files as they are: fields, lines, the job log, the tracks and the worked area of each field,
    /// so restoring it on another tablet gives an exact copy, jobs still linked to their fields.
    /// </summary>
    public static class Backup
    {
        public const string Type = "AgOpenGPSAndroidBackup";

        /// <summary>The data files a full backup holds, relative to the app's data folder.</summary>
        public const string FieldsFile = "fields.json", LinesFile = "ablines.json", JobsFile = "jobs.json";
        public const string TracksFolder = "tracks", CoverageFolder = "coverage";

        /// <summary>
        /// True for the files a backup may contain and a restore may write: the three stores, and
        /// "tracks/&lt;id&gt;.trk" / "coverage/&lt;id&gt;.bin" with a plain id. Nothing else, so a
        /// changed or foreign file cannot write anywhere else on the tablet.
        /// </summary>
        public static bool IsDataFile(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return false;
            if (relativePath == FieldsFile || relativePath == LinesFile || relativePath == JobsFile) return true;
            string[] parts = relativePath.Split('/');
            if (parts.Length != 2) return false;
            string ext = parts[0] == TracksFolder ? ".trk" : parts[0] == CoverageFolder ? ".bin" : null;
            if (ext == null || !parts[1].EndsWith(ext, StringComparison.Ordinal)) return false;
            string id = parts[1].Substring(0, parts[1].Length - ext.Length);
            return id.Length >= 1 && id.Length <= 64 && id.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-');
        }

        /// <summary>A version 2 backup: settings and the data files (relative path to contents).</summary>
        public static string WriteFull(IDictionary<string, object> settings, IDictionary<string, byte[]> files, DateTime nowUtc)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("type", Type);
                    w.WriteNumber("version", 2);
                    w.WriteString("created", nowUtc.ToString("o", CultureInfo.InvariantCulture));
                    WriteSettings(w, settings);
                    w.WriteStartObject("files");
                    foreach (KeyValuePair<string, byte[]> f in files.OrderBy(f => f.Key, StringComparer.Ordinal))
                    {
                        if (!IsDataFile(f.Key) || f.Value == null) continue;
                        w.WriteString(f.Key, Convert.ToBase64String(f.Value));
                    }
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        private static void WriteSettings(Utf8JsonWriter w, IDictionary<string, object> settings)
        {
            w.WriteStartObject("settings");
            foreach (KeyValuePair<string, object> kv in settings)
            {
                if (kv.Value is bool b) w.WriteBoolean(kv.Key, b);
                else if (kv.Value != null) w.WriteString(kv.Key, Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
            }
            w.WriteEndObject();
        }

        /// <param name="settings">Setting name to value (string or bool).</param>
        public static string Write(IDictionary<string, object> settings, string fieldsGeoJson, DateTime nowUtc)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    w.WriteString("type", Type);
                    w.WriteNumber("version", 1);
                    w.WriteString("created", nowUtc.ToString("o", CultureInfo.InvariantCulture));
                    WriteSettings(w, settings);
                    w.WritePropertyName("fields");
                    if (string.IsNullOrWhiteSpace(fieldsGeoJson)) w.WriteNullValue();
                    else using (JsonDocument f = JsonDocument.Parse(fieldsGeoJson)) f.RootElement.WriteTo(w);
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>Reads a backup. Throws FormatException if the file is not one.</summary>
        public static void Read(string text, out Dictionary<string, object> settings, out string fieldsGeoJson)
            => Read(text, out settings, out fieldsGeoJson, out _);

        /// <summary>
        /// Reads a backup of either version. <paramref name="files"/> is null for a version 1 file
        /// (settings and fields only); files that are not data files are skipped.
        /// </summary>
        public static void Read(string text, out Dictionary<string, object> settings, out string fieldsGeoJson,
            out Dictionary<string, byte[]> files)
        {
            settings = new Dictionary<string, object>();
            fieldsGeoJson = null;
            files = null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text ?? ""); }
            catch (JsonException) { throw new FormatException("Not an AgOpenGPS backup file"); }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement t) || t.GetString() != Type)
                    throw new FormatException("Not an AgOpenGPS backup file");
                if (root.TryGetProperty("settings", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in s.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.True || p.Value.ValueKind == JsonValueKind.False) settings[p.Name] = p.Value.GetBoolean();
                        else if (p.Value.ValueKind == JsonValueKind.String) settings[p.Name] = p.Value.GetString();
                    }
                }
                if (root.TryGetProperty("fields", out JsonElement f) && f.ValueKind == JsonValueKind.Object)
                    fieldsGeoJson = f.GetRawText();
                if (root.TryGetProperty("files", out JsonElement fs) && fs.ValueKind == JsonValueKind.Object)
                {
                    files = new Dictionary<string, byte[]>();
                    foreach (JsonProperty p in fs.EnumerateObject())
                    {
                        if (!IsDataFile(p.Name) || p.Value.ValueKind != JsonValueKind.String) continue;
                        try { files[p.Name] = Convert.FromBase64String(p.Value.GetString()); }
                        catch (FormatException) { throw new FormatException("The backup file is damaged"); }
                    }
                }
            }
        }
    }
}
