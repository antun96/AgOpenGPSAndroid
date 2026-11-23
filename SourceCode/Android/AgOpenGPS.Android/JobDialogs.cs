using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Android.App;
using Android.Text;
using Android.Views;
using Android.Widget;
using AgOpenGPS.Mobile.Core;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// The job dialogs shared by the driving screen and the Records screen: the job list line,
    /// choosing the work type, editing a job (work type, note, products applied), deleting, exporting.
    /// </summary>
    public static class JobDialogs
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Shorten(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max - 1) + "…";

        /// <summary>"▶ 2026-05-10 09:12  North · Spraying   4.50 ha   1:05 h:mm   8.2 km/h" (+ the note).</summary>
        public static string Line(Job j, Job current)
        {
            return Tr.F("{0}{1}  {2}   {3:0.00} ha   {4} h:mm   {5} km/h",
                current != null && j.Id == current.Id ? "▶ " : "", j.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv),
                string.IsNullOrEmpty(j.WorkType) ? j.FieldName : j.FieldName + " · " + j.WorkType,
                j.Hectares, JobLog.Duration(j.WorkingSeconds),
                double.IsNaN(j.AverageSpeedKmh) ? "-" : j.AverageSpeedKmh.ToString("0.0", Inv))
                + (j.Inputs != null && j.Inputs.Count > 0 ? "\n" + string.Join(", ", j.Inputs.Select(i => i.Product)) : "")
                + (string.IsNullOrEmpty(j.Note) ? "" : "\n" + Shorten(j.Note, 80));
        }

        /// <summary>The work types to offer: the common ones (translated), then the driver's own, newest first.</summary>
        public static List<string> WorkTypeChoices()
        {
            List<string> list = JobLog.DefaultWorkTypes.Select(Tr.T).ToList();
            list.AddRange(AppState.Jobs.UsedWorkTypes(list));
            return list;
        }

        /// <summary>Just after a job started: what is being done? One tap, Other..., or Skip.</summary>
        public static void ChooseWorkType(Activity a, Job job)
        {
            List<string> choices = WorkTypeChoices();
            var items = choices.Concat(new[] { Tr.T("Other...") }).ToArray();
            new AlertDialog.Builder(a)
                .SetTitle(Tr.F("What are you doing in {0}?", job.FieldName))
                .SetItems(items, (s, e) =>
                {
                    if (e.Which < choices.Count) AppState.Jobs.SetDetails(job.Id, choices[e.Which], job.Note);
                    else AskText(a, Tr.T("Work type"), "", text => AppState.Jobs.SetDetails(job.Id, text, job.Note));
                })
                .SetNegativeButton(Tr.T("Skip"), (s, e) => { })
                .Show();
        }

        private static void AskText(Activity a, string title, string initial, Action<string> ok)
        {
            var input = new EditText(a) { Text = initial };
            input.SetSingleLine(true);
            var frame = new FrameLayout(a);
            frame.SetPadding(Ui.Dp(a, 20), Ui.Dp(a, 8), Ui.Dp(a, 20), 0);
            frame.AddView(input);
            new AlertDialog.Builder(a)
                .SetTitle(title)
                .SetView(frame)
                .SetPositiveButton(Tr.T("OK"), (s, e) => ok(input.Text?.Trim() ?? ""))
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        private static double ParseNumber(string text)
        {
            string t = (text ?? "").Trim().Replace(',', '.');
            return double.TryParse(t, NumberStyles.Float, Inv, out double v) && v >= 0 ? v : double.NaN;
        }

        /// <summary>A job: work type, note, products applied with dose, spray water; Save, Delete, Cancel.</summary>
        public static void Edit(Activity a, Job job, Action changed = null)
        {
            int pad = Ui.Dp(a, 20);
            var box = new LinearLayout(a) { Orientation = Orientation.Vertical };
            box.SetPadding(pad, Ui.Dp(a, 8), pad, 0);
            box.AddView(Ui.Hint(a, Tr.F("{0}   {1} - {2}   {3:0.00} ha", job.FieldName,
                job.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv), job.EndUtc.ToLocalTime().ToString("HH:mm", Inv), job.Hectares)
                + (string.IsNullOrEmpty(job.Crop) ? "" : "   " + job.Crop)));

            box.AddView(Ui.Hint(a, Tr.T("Work type")));
            var type = new AutoCompleteTextView(a) { Text = job.WorkType, Threshold = 0 };
            type.SetSingleLine(true);
            type.Adapter = new ArrayAdapter<string>(a, global::Android.Resource.Layout.SimpleDropDownItem1Line, WorkTypeChoices());
            type.Click += (s, e) => type.ShowDropDown();
            box.AddView(type);

            box.AddView(Ui.Hint(a, Tr.T("Note (seed, rate, weather, problems...)")));
            var note = new EditText(a) { Text = job.Note };
            note.SetMinLines(2);
            note.SetMaxLines(8);
            note.InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences;
            note.Gravity = GravityFlags.Top | GravityFlags.Start;
            box.AddView(note);

            // Products applied: one row each (product, dose, unit, against what).
            box.AddView(Ui.Heading(a, Tr.T("Products applied (spray, fertiliser, seed)")));
            var rows = new LinearLayout(a) { Orientation = Orientation.Vertical };
            box.AddView(rows);
            var editors = new List<(AutoCompleteTextView Product, EditText Dose, Spinner Unit, EditText Target, View Row)>();
            List<string> products = AppState.Jobs.UsedProducts();
            string[] unitNames = JobInput.Units;

            void AddRow(JobInput input)
            {
                var row = new LinearLayout(a) { Orientation = Orientation.Vertical };
                row.SetPadding(0, Ui.Dp(a, 6), 0, Ui.Dp(a, 6));
                var line1 = new LinearLayout(a) { Orientation = Orientation.Horizontal };
                var product = new AutoCompleteTextView(a) { Text = input?.Product ?? "", Hint = Tr.T("Product"), Threshold = 1 };
                product.SetSingleLine(true);
                product.Adapter = new ArrayAdapter<string>(a, global::Android.Resource.Layout.SimpleDropDownItem1Line, products);
                line1.AddView(product, Ui.Weight(3));
                var dose = new EditText(a) { Text = input != null && input.Dose > 0 ? input.Dose.ToString("0.###", Inv) : "", Hint = Tr.T("Dose") };
                dose.InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
                line1.AddView(dose, Ui.Weight(1.3f));
                var unit = new Spinner(a);
                var ua = new ArrayAdapter<string>(a, global::Android.Resource.Layout.SimpleSpinnerItem, unitNames);
                ua.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
                unit.Adapter = ua;
                unit.SetSelection(Math.Max(0, Array.IndexOf(unitNames, input?.Unit ?? "l/ha")));
                line1.AddView(unit, Ui.Weight(1.5f));
                row.AddView(line1);
                var target = new EditText(a) { Text = input?.Target ?? "", Hint = Tr.T("Against / purpose (weed, pest, disease...)") };
                target.SetSingleLine(true);
                row.AddView(target);
                var entry = (product, dose, unit, target, (View)row);
                row.AddView(Ui.Button(a, Tr.T("Remove product"), () =>
                {
                    rows.RemoveView(row);
                    editors.Remove(entry);
                }, 36));
                rows.AddView(row);
                editors.Add(entry);
            }
            foreach (JobInput i in job.Inputs ?? new List<JobInput>()) AddRow(i);
            box.AddView(Ui.Button(a, Tr.T("+ Add product"), () => AddRow(null), 44));
            var water = Ui.Field(a, box, Tr.T("Spray water (l/ha, empty if none)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            water.Text = job.WaterLitersPerHa.HasValue ? job.WaterLitersPerHa.Value.ToString("0", Inv) : "";

            var scroll = new ScrollView(a);
            scroll.AddView(box);
            new AlertDialog.Builder(a)
                .SetTitle(Tr.T("Job"))
                .SetView(scroll)
                .SetPositiveButton(Tr.T("Save"), (s, e) =>
                {
                    AppState.Jobs.SetDetails(job.Id, type.Text, note.Text);
                    var inputs = editors.Select(x => new JobInput
                    {
                        Product = x.Product.Text?.Trim() ?? "",
                        Dose = double.IsNaN(ParseNumber(x.Dose.Text)) ? 0 : ParseNumber(x.Dose.Text),
                        Unit = unitNames[Math.Max(0, x.Unit.SelectedItemPosition)],
                        Target = x.Target.Text?.Trim() ?? "",
                    }).ToList();
                    double w = ParseNumber(water.Text);
                    AppState.Jobs.SetInputs(job.Id, inputs, double.IsNaN(w) ? (double?)null : w);
                    changed?.Invoke();
                })
                .SetNeutralButton(Tr.T("Delete"), (s, e) => ConfirmDelete(a, job, changed))
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        public static void ConfirmDelete(Activity a, Job job, Action changed = null)
        {
            new AlertDialog.Builder(a)
                .SetTitle(Tr.F("Delete the job of {0} ({1})?", job.FieldName, job.StartUtc.ToLocalTime().ToString("yyyy-MM-dd", Inv)))
                .SetPositiveButton(Tr.T("Delete"), (s, e) =>
                {
                    AppState.Jobs.Delete(job.Id);
                    changed?.Invoke();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        /// <summary>Saves <paramref name="jobs"/> as CSV, PDF, KML or GeoJSON, as the driver chooses.</summary>
        public static void Export(Activity a, IReadOnlyList<Job> jobs)
        {
            JobLog log = AppState.Jobs;
            log.Save();
            string day = DateTime.Now.ToString("yyyy-MM-dd", Inv);
            new AlertDialog.Builder(a)
                .SetTitle(Tr.F("Export {0} jobs", jobs.Count))
                .SetItems(new[] { Tr.T("CSV (Excel)"), Tr.T("PDF (print, with a map of each job)"), Tr.T("Track: KML (Google Earth)"), Tr.T("Track: GeoJSON (QGIS, farm software)") }, (s, e) =>
                {
                    if (e.Which == 0)
                    {
                        FileSaver.Save(a, Tr.T("Job log") + " " + day + ".csv", "text/csv", Csv(JobLog.ToCsv(jobs, Tr.T)), Tr.T("Job log saved"));
                    }
                    else if (e.Which == 1)
                    {
                        byte[] pdf = JobReport.Pdf(jobs, j => log.Track(j.Id),
                            j => AppState.Fields.Fields.FirstOrDefault(f => f.Id == j.FieldId)?.Boundary);
                        FileSaver.Save(a, Tr.T("Job log") + " " + day + ".pdf", "application/pdf", pdf, Tr.T("Job log saved"));
                    }
                    else
                    {
                        List<JobTrack> tracks = jobs.Select(j => new JobTrack { Job = j, Pieces = log.Track(j.Id) }).Where(t => t.HasTrack).ToList();
                        if (tracks.Count == 0)
                        {
                            Toast.MakeText(a, Tr.T("No tracks yet: a track is recorded while Work is on"), ToastLength.Long).Show();
                            return;
                        }
                        if (e.Which == 2)
                            FileSaver.Save(a, Tr.T("Tracks") + " " + day + ".kml", "application/vnd.google-earth.kml+xml",
                                System.Text.Encoding.UTF8.GetBytes(TrackExport.ToKml(tracks, Tr.T)), Tr.T("Tracks saved"));
                        else
                            FileSaver.Save(a, Tr.T("Tracks") + " " + day + ".geojson", "application/geo+json",
                                System.Text.Encoding.UTF8.GetBytes(TrackExport.ToGeoJson(tracks)), Tr.T("Tracks saved"));
                    }
                })
                .Show();
        }

        /// <summary>UTF-8 with BOM, so Excel shows č, ć, š, ž, đ correctly.</summary>
        public static byte[] Csv(string text) =>
            new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray();
    }
}
