using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Widget;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// Backing up everything to one file and restoring it, from Settings or Records. A full backup
    /// (version 2) replaces all data on restore; an older backup (settings and fields only) adds
    /// its fields as before.
    /// </summary>
    public static class DataBackup
    {
        public const int RestoreRequest = 41;

        public static void Save(Activity a)
        {
            Dictionary<string, byte[]> files = AppState.CollectData();
            string text = Backup.WriteFull(AppState.Settings.ExportAll(), files, DateTime.UtcNow);
            FileSaver.Save(a, "AgOpenGPS backup " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json",
                "application/json", new System.Text.UTF8Encoding(false).GetBytes(text), Tr.T("Backup saved"));
        }

        public static void Pick(Activity a)
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            a.StartActivityForResult(intent, RestoreRequest);
        }

        private static int Count(Dictionary<string, byte[]> files, string name, string array)
        {
            if (!files.TryGetValue(name, out byte[] bytes)) return 0;
            try
            {
                using (JsonDocument d = JsonDocument.Parse(bytes))
                    return d.RootElement.TryGetProperty(array, out JsonElement e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
            }
            catch (JsonException) { return 0; }
        }

        /// <summary>True when the result was a restore pick (handled here). <paramref name="restored"/> runs after a restore.</summary>
        public static bool HandleResult(Activity a, int requestCode, Result resultCode, Intent data, Action restored)
        {
            if (requestCode != RestoreRequest) return false;
            if (resultCode != Result.Ok || data?.Data == null) return true;

            Dictionary<string, object> settings;
            string fieldsJson;
            Dictionary<string, byte[]> files;
            try
            {
                string text;
                using (var stream = a.ContentResolver.OpenInputStream(data.Data))
                using (var reader = new System.IO.StreamReader(stream))
                    text = reader.ReadToEnd();
                Backup.Read(text, out settings, out fieldsJson, out files);
            }
            catch (Exception ex)
            {
                Toast.MakeText(a, Tr.T(ex.Message), ToastLength.Long).Show();
                return true;
            }

            void Done()
            {
                if (AppState.IsConnected) Task.Run(AppState.Reconnect); // with the restored NTRIP settings
                Toast.MakeText(a, Tr.T("Backup restored"), ToastLength.Long).Show();
                restored?.Invoke();
            }

            if (files != null)
            {
                int fields = Count(files, Backup.FieldsFile, "Fields"), jobs = Count(files, Backup.JobsFile, "Jobs");
                new AlertDialog.Builder(a)
                    .SetTitle(Tr.T("Replace everything on this tablet?"))
                    .SetMessage(Tr.F("The backup has {0} fields and {1} jobs. All settings, fields, lines, jobs, tracks and worked areas on this tablet are replaced by it. The paired Bluetooth device is kept.", fields, jobs))
                    .SetPositiveButton(Tr.T("Replace"), (s, e) =>
                    {
                        AppState.Settings.ImportAll(settings);
                        AppState.ReloadSettings();
                        AppState.ReplaceData(files);
                        Done();
                    })
                    .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                    .Show();
                return true;
            }

            // An older backup: settings plus fields, which are added to the ones here.
            List<ImportedField> imported = new List<ImportedField>();
            if (fieldsJson != null)
            {
                try { imported = FieldImport.Parse(fieldsJson, "Field"); }
                catch (FormatException) { /* a backup without fields */ }
            }
            new AlertDialog.Builder(a)
                .SetTitle(Tr.T("Restore this backup?"))
                .SetMessage(Tr.F("All settings are replaced; {0} fields are added (existing fields stay). The paired Bluetooth device is kept.", imported.Count))
                .SetPositiveButton(Tr.T("Restore"), (s, e) =>
                {
                    AppState.Settings.ImportAll(settings);
                    AppState.ReloadSettings();
                    if (imported.Count > 0) AppState.ImportFields(imported);
                    Done();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
            return true;
        }
    }
}
