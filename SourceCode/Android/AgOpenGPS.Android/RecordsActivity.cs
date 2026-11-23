using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// Records: everything the app keeps, in one place. Tabs: fields (with crop and notes), jobs
    /// (filtered, with totals and export), the season summary, the spray and fertiliser record,
    /// the guidance lines, and the data page (full backup and restore, storage).
    /// </summary>
    [Activity(Label = "Records", Theme = "@android:style/Theme.Material.Light.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
    public class RecordsActivity : Activity
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private enum Tab { Fields, Jobs, Season, Inputs, Lines, Data }
        private static Tab _tab = Tab.Fields; // kept while the app runs

        private LinearLayout _content;
        private ScrollView _scroll;
        private readonly Dictionary<Tab, Button> _tabButtons = new Dictionary<Tab, Button>();

        // Jobs filter, kept while the app runs.
        private static string _filterField;   // null = all
        private static int _filterYear;       // 0 = all
        private static string _filterType;    // null = all
        private static int _seasonYear = DateTime.Now.Year, _inputsYear = DateTime.Now.Year;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            AppState.Init(this);
            SetContentView(BuildLayout());
            Show(_tab);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (FileSaver.HandleResult(this, requestCode, resultCode, data)) return;
            DataBackup.HandleResult(this, requestCode, resultCode, data, () => Show(_tab));
        }

        private int Dp(float v) => Ui.Dp(this, v);

        private View BuildLayout()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(Color.ParseColor("#F2F2F2"));

            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetBackgroundColor(Color.ParseColor("#37474F"));
            bar.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.AddView(Ui.Button(this, Tr.T("< Back"), Finish, 48));
            TextView title = Ui.Text(this, 22, true, "#FFFFFF");
            title.Text = "  " + Tr.T("Records");
            bar.AddView(title, Ui.Weight(1));
            root.AddView(bar);

            var tabs = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            tabs.SetPadding(Dp(8), Dp(4), Dp(8), 0);
            void TabButton(Tab t, string text)
            {
                Button b = Ui.Button(this, text, () => Show(t), 48);
                _tabButtons[t] = b;
                tabs.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(4) });
            }
            TabButton(Tab.Fields, Tr.T("Fields"));
            TabButton(Tab.Jobs, Tr.T("Jobs"));
            TabButton(Tab.Season, Tr.T("Season"));
            TabButton(Tab.Inputs, Tr.T("Spray & fertiliser"));
            TabButton(Tab.Lines, Tr.T("Lines"));
            TabButton(Tab.Data, Tr.T("Data"));
            var tabScroll = new HorizontalScrollView(this);
            tabScroll.AddView(tabs);
            root.AddView(tabScroll);

            _content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _content.SetPadding(Dp(16), Dp(8), Dp(16), Dp(24));
            _scroll = new ScrollView(this);
            _scroll.AddView(_content);
            root.AddView(_scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
            return root;
        }

        private void Show(Tab tab)
        {
            _tab = tab;
            foreach (var kv in _tabButtons)
            {
                kv.Value.SetBackgroundColor(Color.ParseColor(kv.Key == tab ? "#1565C0" : "#E0E0E0"));
                kv.Value.SetTextColor(kv.Key == tab ? Color.White : Color.ParseColor("#212121"));
            }
            _content.RemoveAllViews();
            switch (tab)
            {
                case Tab.Fields: ShowFields(); break;
                case Tab.Jobs: ShowJobs(); break;
                case Tab.Season: ShowSeason(); break;
                case Tab.Inputs: ShowInputs(); break;
                case Tab.Lines: ShowLines(); break;
                case Tab.Data: ShowData(); break;
            }
        }

        private void Refresh() => Show(_tab);

        // ---------------------------------------------------------------- building blocks

        /// <summary>A white card with a bold first line and grey lines below; tapping it runs <paramref name="tap"/>.</summary>
        private LinearLayout Row(string first, string rest, Action tap)
        {
            LinearLayout card = Ui.Card(this);
            card.SetPadding(Dp(16), Dp(10), Dp(16), Dp(10));
            TextView a = Ui.Text(this, 17, true);
            a.Text = first;
            card.AddView(a);
            if (!string.IsNullOrEmpty(rest)) card.AddView(Ui.Hint(this, rest));
            if (tap != null)
            {
                card.Clickable = true;
                card.Click += (s, e) => tap();
            }
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) };
            _content.AddView(card, lp);
            return card;
        }

        private void Text(string text, float sp = 15, bool bold = false)
        {
            TextView t = Ui.Text(this, sp, bold);
            t.Text = text;
            _content.AddView(t);
        }

        /// <summary>A drop-down with labels; calls <paramref name="chosen"/> with the index.</summary>
        private Spinner Choice(LinearLayout parent, IList<string> labels, int selected, Action<int> chosen)
        {
            var sp = new Spinner(this);
            var ad = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, labels.ToList());
            ad.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            sp.Adapter = ad;
            sp.SetSelection(Math.Max(0, Math.Min(labels.Count - 1, selected)));
            bool first = true;
            sp.ItemSelected += (s, e) =>
            {
                if (first) { first = false; return; } // the initial selection is not a choice
                chosen(e.Position);
            };
            parent.AddView(sp, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(12) });
            return sp;
        }

        private LinearLayout ButtonRow(params (string Text, Action Click)[] buttons)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            foreach (var (text, click) in buttons)
                row.AddView(Ui.Button(this, text, click, 48), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(8) });
            _content.AddView(row);
            return row;
        }

        private FieldPreviewView Preview(LinearLayout parent, float heightDp)
        {
            var v = new FieldPreviewView(this);
            parent.AddView(v, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(heightDp)) { BottomMargin = Dp(8) });
            return v;
        }

        private List<int> YearsWithJobs() =>
            AppState.Jobs.Jobs.Select(j => j.StartUtc.ToLocalTime().Year).Append(DateTime.Now.Year).Distinct().OrderByDescending(y => y).ToList();

        // ---------------------------------------------------------------- fields

        private void ShowFields()
        {
            IReadOnlyList<Field> fields = AppState.Fields.Fields;
            IReadOnlyList<Job> jobs = AppState.Jobs.Jobs;
            string open = AppState.Fields.Active?.Id;
            double totalHa = fields.Where(f => f.HasBoundary).Sum(f => f.AreaHectares);
            Text(Tr.F("{0} fields, {1:0.00} ha", fields.Count, totalHa), 16, true);
            ButtonRow((Tr.T("New field"), NewField));
            if (fields.Count == 0) Text(Tr.T("No fields yet. Create one here or on the driving screen, or import them from a file."));

            foreach (Field f in fields)
            {
                Job last = jobs.Where(j => j.FieldId == f.Id).OrderByDescending(j => j.StartUtc).FirstOrDefault();
                int lines = AppState.Lines.LinesFor(f.Id).Count;
                string crop = string.IsNullOrEmpty(f.Crop) ? "" : "   " + f.Crop + (string.IsNullOrEmpty(f.Variety) ? "" : " (" + f.Variety + ")");
                string first = (f.Id == open ? "▶ " : "") + f.Name + "   " + (f.HasBoundary ? f.AreaHectares.ToString("0.00", Inv) + " ha" : Tr.T("no boundary")) + crop;
                string rest = Tr.F("Lines: {0}", lines)
                    + (last == null ? "   " + Tr.T("no jobs yet") : "   " + Tr.F("Last job: {0} {1}", last.StartUtc.ToLocalTime().ToString("yyyy-MM-dd", Inv), last.WorkType))
                    + (string.IsNullOrEmpty(f.Note) ? "" : "\n" + JobDialogs.Shorten(f.Note, 100));
                Row(first, rest, () => EditField(f));
            }
        }

        private void NewField()
        {
            var input = new EditText(this) { Text = AppState.Fields.NextName() };
            input.SetSingleLine(true);
            var frame = new FrameLayout(this);
            frame.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            frame.AddView(input);
            new AlertDialog.Builder(this)
                .SetTitle(Tr.T("New field"))
                .SetView(frame)
                .SetPositiveButton(Tr.T("OK"), (s, e) =>
                {
                    Field f = AppState.Fields.Create(input.Text);
                    AppState.OpenField(f);
                    Refresh();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        private void EditField(Field f)
        {
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            FieldPreviewView preview = Preview(box, 220);
            List<Job> jobs = AppState.Jobs.Jobs.Where(j => j.FieldId == f.Id).ToList();
            Job last = jobs.FirstOrDefault();
            preview.Show(f.Boundary, AppState.Lines.LinesFor(f.Id), last == null ? null : AppState.Jobs.Track(last.Id), last?.ImplementWidth ?? 0);
            if (last != null) box.AddView(Ui.Hint(this, Tr.F("Green: the last job, {0}", last.StartUtc.ToLocalTime().ToString("yyyy-MM-dd", Inv))));

            JobTotals t = JobTotals.Of(jobs.Where(j => j.StartUtc.ToLocalTime().Year == DateTime.Now.Year));
            box.AddView(Ui.Hint(this, Tr.F("This year: {0} jobs, {1:0.00} ha, {2} h:mm", t.Count, t.Hectares, JobLog.Duration(t.Hours * 3600))));

            EditText name = Ui.Field(this, box, Tr.T("Name"), InputTypes.ClassText | InputTypes.TextFlagCapSentences);
            name.Text = f.Name;
            EditText crop = Ui.Field(this, box, Tr.T("Crop this season"), InputTypes.ClassText | InputTypes.TextFlagCapSentences);
            crop.Text = f.Crop;
            EditText variety = Ui.Field(this, box, Tr.T("Variety"), InputTypes.ClassText | InputTypes.TextFlagCapSentences);
            variety.Text = f.Variety;
            EditText note = Ui.Field(this, box, Tr.T("Notes (soil, drainage, owner or lease...)"), InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences);
            note.SetSingleLine(false);
            note.SetMinLines(2);
            note.Text = f.Note;

            var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            box.AddView(buttons);
            var scroll = new ScrollView(this);
            scroll.AddView(box);
            AlertDialog dialog = new AlertDialog.Builder(this)
                .SetTitle(f.Name)
                .SetView(scroll)
                .SetPositiveButton(Tr.T("Save"), (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(name.Text) && name.Text.Trim() != f.Name) AppState.Fields.Rename(f.Id, name.Text);
                    AppState.Fields.SetDetails(f.Id, crop.Text, variety.Text, note.Text);
                    AppState.RefreshField();
                    Refresh();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Create();
            buttons.AddView(Ui.Button(this, Tr.T("Open"), () =>
            {
                AppState.OpenField(f);
                dialog.Dismiss();
                Finish(); // back to the driving screen, in that field
            }, 44));
            buttons.AddView(Ui.Button(this, Tr.T("Export"), () =>
            {
                string json = FieldExport.ToGeoJson(new[] { f }, id => AppState.Lines.LinesFor(id));
                FileSaver.Save(this, f.Name + ".geojson", "application/geo+json", System.Text.Encoding.UTF8.GetBytes(json), Tr.T("Field saved"));
            }, 44));
            buttons.AddView(Ui.Button(this, Tr.T("Delete"), () =>
            {
                new AlertDialog.Builder(this)
                    .SetTitle(Tr.F("Delete {0}?", f.Name))
                    .SetMessage(Tr.T("Its boundary, lines and worked area are deleted. Its jobs stay in the job log."))
                    .SetPositiveButton(Tr.T("Delete"), (s, e) =>
                    {
                        AppState.DeleteField(f);
                        dialog.Dismiss();
                        Refresh();
                    })
                    .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                    .Show();
            }, 44));
            dialog.Show();
        }

        // ---------------------------------------------------------------- jobs

        private void ShowJobs()
        {
            IReadOnlyList<Field> fields = AppState.Fields.Fields;
            List<int> years = YearsWithJobs();
            List<string> types = AppState.Jobs.Jobs.Select(j => (j.WorkType ?? "").Trim()).Where(w => w.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToList();

            var filters = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _content.AddView(filters);
            var fieldLabels = new List<string> { Tr.T("All fields") };
            fieldLabels.AddRange(fields.Select(f => f.Name));
            Choice(filters, fieldLabels, _filterField == null ? 0 : fields.ToList().FindIndex(f => f.Id == _filterField) + 1, i =>
            {
                _filterField = i == 0 ? null : fields[i - 1].Id;
                Refresh();
            });
            var yearLabels = new List<string> { Tr.T("All years") };
            yearLabels.AddRange(years.Select(y => y.ToString(Inv)));
            Choice(filters, yearLabels, _filterYear == 0 ? 0 : years.IndexOf(_filterYear) + 1, i =>
            {
                _filterYear = i == 0 ? 0 : years[i - 1];
                Refresh();
            });
            var typeLabels = new List<string> { Tr.T("All work") };
            typeLabels.AddRange(types);
            Choice(filters, typeLabels, _filterType == null ? 0 : types.FindIndex(w => string.Equals(w, _filterType, StringComparison.OrdinalIgnoreCase)) + 1, i =>
            {
                _filterType = i == 0 ? null : types[i - 1];
                Refresh();
            });

            var filter = new JobFilter { FieldId = _filterField, WorkType = _filterType };
            if (_filterYear > 0) { filter.FromLocal = new DateTime(_filterYear, 1, 1); filter.ToLocal = new DateTime(_filterYear, 12, 31); }
            List<Job> jobs = filter.Apply(AppState.Jobs.Jobs);
            JobTotals t = JobTotals.Of(jobs);
            Text(Tr.F("{0} jobs, {1:0.00} ha, {2} h:mm, {3:0.0} km", t.Count, t.Hectares, JobLog.Duration(t.Hours * 3600), t.Kilometres), 16, true);
            if (jobs.Count > 0) ButtonRow((Tr.F("Export {0} jobs", jobs.Count), () => JobDialogs.Export(this, jobs)));
            else Text(Tr.T("No jobs here. A job starts with Work: ON in a field."));

            Job current = AppState.Jobs.Current;
            foreach (Job j in jobs)
            {
                string[] parts = JobDialogs.Line(j, current).Split('\n');
                Row(parts[0], string.Join("\n", parts.Skip(1)), () => ShowJob(j));
            }
        }

        /// <summary>The job on a map, then its details to edit.</summary>
        private void ShowJob(Job j)
        {
            Field f = AppState.Fields.Fields.FirstOrDefault(x => x.Id == j.FieldId);
            List<TrackPiece> track = AppState.Jobs.Track(j.Id);
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            Preview(box, 260).Show(f?.Boundary, null, track, j.ImplementWidth);
            box.AddView(Ui.Hint(this, TrackExport.Summary(j, Tr.T)));
            if (track.Count > 0)
                box.AddView(Ui.Hint(this, string.Join("\n", track.Select((p, i) => Tr.F("Part {0}: {1} - {2}", i + 1,
                    p.StartUtc.ToLocalTime().ToString("HH:mm", Inv), p.EndUtc.ToLocalTime().ToString("HH:mm", Inv))))));
            var scroll = new ScrollView(this);
            scroll.AddView(box);
            new AlertDialog.Builder(this)
                .SetTitle(j.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Inv) + "  " + j.FieldName + (string.IsNullOrEmpty(j.WorkType) ? "" : "  " + j.WorkType))
                .SetView(scroll)
                .SetPositiveButton(Tr.T("Edit..."), (s, e) => JobDialogs.Edit(this, j, Refresh))
                .SetNeutralButton(Tr.T("Export..."), (s, e) => JobDialogs.Export(this, new[] { j }))
                .SetNegativeButton(Tr.T("Close"), (s, e) => { })
                .Show();
        }

        // ---------------------------------------------------------------- season

        private void ShowSeason()
        {
            List<int> years = YearsWithJobs();
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _content.AddView(top);
            Choice(top, years.Select(y => y.ToString(Inv)).ToList(), Math.Max(0, years.IndexOf(_seasonYear)), i =>
            {
                _seasonYear = years[i];
                Refresh();
            });
            SeasonSummary sum = SeasonSummary.Build(_seasonYear, AppState.Fields.Fields, AppState.Jobs.Jobs);
            top.AddView(Ui.Button(this, Tr.T("Export CSV"), () => FileSaver.Save(this,
                Tr.F("Season {0}", _seasonYear) + ".csv", "text/csv", JobDialogs.Csv(sum.ToCsv(Tr.T)), Tr.T("Season saved")), 44));

            Text(Tr.F("{0}: {1} jobs, {2:0.00} ha, {3} h:mm", _seasonYear, sum.Total.Count, sum.Total.Hectares, JobLog.Duration(sum.Total.Hours * 3600)), 16, true);
            if (sum.WorkTypes.Count == 0) Text(Tr.T("Nothing done yet this year."));

            foreach (SeasonRow r in sum.Rows)
            {
                string first = r.FieldName + (double.IsNaN(r.AreaHectares) ? "" : "   " + r.AreaHectares.ToString("0.00", Inv) + " ha")
                    + (string.IsNullOrEmpty(r.Crop) ? "" : "   " + r.Crop);
                var lines = new List<string>();
                foreach (string w in sum.WorkTypes)
                {
                    string name = w.Length == 0 ? Tr.T("No work type") : w;
                    if (r.Cells.TryGetValue(w, out SeasonCell c))
                    {
                        string share = double.IsNaN(r.AreaHectares) || r.AreaHectares <= 0 ? "" : string.Format(Inv, "  ({0:0} %)", Math.Min(999, c.Hectares / r.AreaHectares * 100));
                        lines.Add(Tr.F("✓ {0}: {1:0.00} ha{2}, {3}", name, c.Hectares, share, c.LastLocal.ToString("yyyy-MM-dd", Inv)));
                    }
                    else lines.Add(Tr.F("– {0}: not done", name));
                }
                Row(first, string.Join("\n", lines), r.FieldId == null ? (Action)null : () =>
                {
                    _filterField = r.FieldId;
                    _filterYear = _seasonYear;
                    _filterType = null;
                    Show(Tab.Jobs);
                });
            }
        }

        // ---------------------------------------------------------------- spray and fertiliser record

        private void ShowInputs()
        {
            List<int> years = YearsWithJobs();
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _content.AddView(top);
            Choice(top, years.Select(y => y.ToString(Inv)).ToList(), Math.Max(0, years.IndexOf(_inputsYear)), i =>
            {
                _inputsYear = years[i];
                Refresh();
            });
            List<InputRecordRow> rows = InputRecord.Rows(JobFilter.Year(_inputsYear).Apply(AppState.Jobs.Jobs));
            if (rows.Count > 0)
            {
                top.AddView(Ui.Button(this, Tr.T("Export CSV"), () => FileSaver.Save(this, Tr.F("Spray and fertiliser record {0}", _inputsYear) + ".csv",
                    "text/csv", JobDialogs.Csv(InputRecord.ToCsv(rows, Tr.T)), Tr.T("Record saved")), 44));
                top.AddView(Ui.Button(this, Tr.T("Export PDF"), () => FileSaver.Save(this, Tr.F("Spray and fertiliser record {0}", _inputsYear) + ".pdf",
                    "application/pdf", JobReport.InputPdf(rows, _inputsYear), Tr.T("Record saved")), 44));
            }
            Text(Tr.T("Products are entered per job: tap a job (Jobs tab, or Jobs... on the driving screen) and add the product, dose and what it was against. Area and totals come from the job."));
            if (rows.Count == 0)
            {
                Text(Tr.F("No products recorded in {0} yet.", _inputsYear), 16, true);
                return;
            }

            Text(Tr.T("Totals"), 17, true);
            foreach (ProductTotal t in InputRecord.Totals(rows))
                Text(Tr.F("{0}: {1:0.##} {2} on {3:0.00} ha ({4}×)", t.Product, t.Amount, t.Unit, t.Hectares, t.Uses));

            Text(Tr.T("Record"), 17, true);
            foreach (InputRecordRow r in rows)
            {
                Job j = r.Job;
                string first = j.StartUtc.ToLocalTime().ToString("yyyy-MM-dd", Inv) + "   " + j.FieldName + (string.IsNullOrEmpty(j.Crop) ? "" : " (" + j.Crop + ")")
                    + "   " + j.Hectares.ToString("0.00", Inv) + " ha";
                string rest = Tr.F("{0}: {1:0.###} {2} = {3:0.##} {4}", r.Input.Product, r.Input.Dose, r.Input.Unit, r.Total, r.Input.TotalUnit)
                    + (j.WaterLitersPerHa.HasValue ? "   " + Tr.F("water {0:0} l/ha", j.WaterLitersPerHa.Value) : "")
                    + (string.IsNullOrEmpty(r.Input.Target) ? "" : "\n" + Tr.F("Against / purpose: {0}", r.Input.Target));
                Row(first, rest, () => JobDialogs.Edit(this, j, Refresh));
            }
        }

        // ---------------------------------------------------------------- lines

        private void ShowLines()
        {
            IReadOnlyList<AbLine> all = AppState.Lines.Lines;
            string active = AppState.Guidance.ActiveLine?.Id;
            Text(Tr.F("{0} lines", all.Count), 16, true);
            if (all.Count == 0) Text(Tr.T("No lines yet: drive to the start, press A, drive along, press B."));
            Dictionary<string, Field> fields = AppState.Fields.Fields.ToDictionary(f => f.Id);
            foreach (var group in all.GroupBy(l => l.FieldId ?? "").OrderBy(g => fields.TryGetValue(g.Key, out Field f) ? f.Name : "~"))
            {
                string fieldName = fields.TryGetValue(group.Key, out Field field) ? field.Name : Tr.T("Without a field");
                Text(fieldName, 17, true);
                foreach (AbLine l in group)
                {
                    string kind = l.Kind == LineKind.Curve ? Tr.T("Curve") : Tr.T("Straight");
                    string nudge = l.NudgeMeters == 0 ? "" : "   " + Tr.F("nudged {0:+0;-0} cm", l.NudgeMeters * 100);
                    Row((l.Id == active ? "▶ " : "") + l.Name, kind + nudge, () => EditLine(l, field));
                }
            }
        }

        private void EditLine(AbLine l, Field field)
        {
            var b = new AlertDialog.Builder(this).SetTitle(l.Name);
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(20), Dp(8), Dp(20), 0);
            Preview(box, 200).Show(field?.Boundary, new[] { l });
            b.SetView(box);
            b.SetPositiveButton(Tr.T("Use this line"), (s, e) =>
            {
                if (field != null && AppState.Fields.Active?.Id != field.Id) AppState.OpenField(field);
                AppState.UseLine(l);
                Finish();
            });
            b.SetNeutralButton(Tr.T("Delete"), (s, e) => new AlertDialog.Builder(this)
                .SetTitle(Tr.F("Delete {0}?", l.Name))
                .SetPositiveButton(Tr.T("Delete"), (s2, e2) =>
                {
                    bool wasActive = AppState.Guidance.ActiveLine?.Id == l.Id;
                    AppState.Lines.Delete(l.Id);
                    if (wasActive) AppState.UseLine(null);
                    Refresh();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s2, e2) => { })
                .Show());
            b.SetNegativeButton(Tr.T("Rename"), (s, e) =>
            {
                var input = new EditText(this) { Text = l.Name };
                input.SetSingleLine(true);
                var frame = new FrameLayout(this);
                frame.SetPadding(Dp(20), Dp(8), Dp(20), 0);
                frame.AddView(input);
                new AlertDialog.Builder(this)
                    .SetTitle(Tr.T("Rename"))
                    .SetView(frame)
                    .SetPositiveButton(Tr.T("OK"), (s2, e2) =>
                    {
                        AppState.Lines.Rename(l.Id, input.Text);
                        Refresh();
                    })
                    .SetNegativeButton(Tr.T("Cancel"), (s2, e2) => { })
                    .Show();
            });
            b.Show();
        }

        // ---------------------------------------------------------------- data

        private static string Size(long bytes) =>
            bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0", Inv) + " MB" : (bytes / 1024.0).ToString("0", Inv) + " kB";

        private void ShowData()
        {
            Text(Tr.T("Move to another tablet"), 17, true);
            Text(Tr.T("Back up everything: one file with all settings, fields with crop and notes, lines, jobs with products and notes, tracks and the worked area of each field. On the other tablet, install the app and press Restore. Keep the file private: it contains the NTRIP password."));
            ButtonRow((Tr.T("Back up everything"), () => DataBackup.Save(this)), (Tr.T("Restore..."), () => DataBackup.Pick(this)));

            var use = AppState.StorageUse();
            Text(Tr.T("Storage on this tablet"), 17, true);
            Text(Tr.F("Fields and lines: {0}", Size(use.Fields)));
            Text(Tr.F("Job log: {0} ({1} jobs)", Size(use.Jobs), AppState.Jobs.Jobs.Count));
            Text(Tr.F("Tracks: {0}", Size(use.Tracks)));
            Text(Tr.F("Worked areas: {0}", Size(use.Coverage)));
            Text(Tr.F("Map cache: {0} (not in the backup; downloaded again when needed)", Size(use.Map)));
            ButtonRow((Tr.T("Clear map cache"), () =>
            {
                AppState.Map?.ClearDisk();
                Refresh();
            }));
        }
    }
}
