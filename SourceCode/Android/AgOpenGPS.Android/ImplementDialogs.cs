using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Android.App;
using Android.Text;
using Android.Widget;
using AgOpenGPS.Mobile.Core;

namespace AgOpenGPS.Droid
{
    /// <summary>Choosing, adding and editing the saved implements: from the Drive tab and from Settings.</summary>
    public static class ImplementDialogs
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>"Planter 6 rows · 4.50 m" for buttons and lists.</summary>
        public static string Short(Implement i) => i == null ? Tr.T("No implement") : i.Name + " · " + i.Width.ToString("0.00", Inv) + " m";

        /// <summary>One line with all values, for lists.</summary>
        public static string Details(Implement i)
        {
            string s = Tr.F("Width {0:0.00} m, overlap {1:0.00} m, passes {2:0.00} m apart", i.Width, i.Overlap, i.PassSpacing);
            if (i.Behind != 0 || i.Right != 0) s += "\n" + Tr.F("Works {0:0.00} m behind the rear axle, {1:+0.00;-0.00;0.00} m to the right", i.Behind, i.Right);
            if (i.HeadlandWidth.HasValue) s += "\n" + Tr.F("Headland {0:0.0} m", i.HeadlandWidth.Value);
            if (!string.IsNullOrEmpty(i.WorkType)) s += "\n" + Tr.F("Work: {0}", i.WorkType);
            return s;
        }

        /// <summary>The list to pick from (Drive tab). Switching while Work is on asks first.</summary>
        public static void Choose(Activity a, Action changed)
        {
            IReadOnlyList<Implement> all = AppState.Implements.Implements;
            string active = AppState.Implements.Active?.Id;
            string[] items = all.Select(i => (i.Id == active ? "▶ " : "") + Short(i)).ToArray();
            new AlertDialog.Builder(a)
                .SetTitle(Tr.T("Implement on the tractor"))
                .SetItems(items, (s, e) =>
                {
                    Implement pick = all[e.Which];
                    if (pick.Id == active) return;
                    void Use()
                    {
                        AppState.UseImplement(pick);
                        Toast.MakeText(a, Tr.F("Now using {0}: passes {1:0.00} m apart", pick.Name, pick.PassSpacing), ToastLength.Long).Show();
                        changed?.Invoke();
                    }
                    if (!AppState.Guidance.Working) Use();
                    else new AlertDialog.Builder(a)
                        .SetTitle(Tr.F("Work is on. Switch to {0}?", pick.Name))
                        .SetMessage(Tr.T("From now on the worked area is painted with the new width."))
                        .SetPositiveButton(Tr.T("Switch"), (s2, e2) => Use())
                        .SetNegativeButton(Tr.T("Cancel"), (s2, e2) => { })
                        .Show();
                })
                .SetNeutralButton(Tr.T("New implement"), (s, e) => Edit(a, null, changed))
                .SetPositiveButton(Tr.T("Edit this one"), (s, e) =>
                {
                    Implement cur = AppState.Implements.Active;
                    if (cur != null) Edit(a, cur, changed);
                })
                .SetNegativeButton(Tr.T("Close"), (s, e) => { })
                .Show();
        }

        private static double Number(EditText e, double fallback, bool signed = false)
        {
            string t = (e.Text ?? "").Trim();
            return signed ? AppSettings.ParseSigned(t, fallback) : AppSettings.ParseMeters(t, fallback);
        }

        /// <summary>Adds a new implement (null) or edits one. The one in use is applied at once.</summary>
        public static void Edit(Activity a, Implement implement, Action changed)
        {
            bool isNew = implement == null;
            Implement i = implement?.Clone() ?? new Implement { Name = AppState.Implements.NextName(), Width = AppState.Settings.ToolWidthMeters };

            var box = new LinearLayout(a) { Orientation = Orientation.Vertical };
            box.SetPadding(Ui.Dp(a, 20), Ui.Dp(a, 8), Ui.Dp(a, 20), 0);
            InputTypes dec = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal;
            InputTypes signed = dec | InputTypes.NumberFlagSigned;

            EditText name = Ui.Field(a, box, Tr.T("Name (e.g. Planter 6 rows)"), InputTypes.ClassText | InputTypes.TextFlagCapSentences);
            name.Text = i.Name;
            EditText width = Ui.Field(a, box, Tr.T("Implement width (m)"), dec);
            width.Text = i.Width.ToString("0.###", Inv);
            box.AddView(Ui.Hint(a, Tr.T("The real full width of the implement. The worked area always uses the full width.")));
            EditText overlap = Ui.Field(a, box, Tr.T("Overlap (m)"), dec);
            overlap.Text = i.Overlap.ToString("0.###", Inv);
            TextView spacing = Ui.Hint(a, "");
            box.AddView(spacing);
            void UpdateSpacing()
            {
                double w = Number(width, double.NaN), o = Number(overlap, double.NaN);
                spacing.Text = double.IsNaN(w) || double.IsNaN(o) ? Tr.T("Enter the width in metres, e.g. 6.0")
                    : string.Format(Inv, Tr.T("Distance between passes: {0:0.00} m"), Math.Max(0.1, w - o));
            }
            width.TextChanged += (s, e) => UpdateSpacing();
            overlap.TextChanged += (s, e) => UpdateSpacing();
            UpdateSpacing();

            EditText behind = Ui.Field(a, box, Tr.T("Implement working line behind the rear axle (front mounted = negative)"), signed);
            behind.Text = i.Behind.ToString("0.###", Inv);
            EditText right = Ui.Field(a, box, Tr.T("Implement centre right of the tractor centre line"), signed);
            right.Text = i.Right.ToString("0.###", Inv);
            EditText headland = Ui.Field(a, box, Tr.T("Headland width with this implement (m, empty = keep the setting)"), dec);
            headland.Text = i.HeadlandWidth.HasValue ? i.HeadlandWidth.Value.ToString("0.###", Inv) : "";

            box.AddView(Ui.Hint(a, Tr.T("Usual work (a new job starts with it; empty = ask)")));
            var work = new AutoCompleteTextView(a) { Text = i.WorkType, Threshold = 0 };
            work.SetSingleLine(true);
            work.Adapter = new ArrayAdapter<string>(a, global::Android.Resource.Layout.SimpleDropDownItem1Line, JobDialogs.WorkTypeChoices());
            work.Click += (s, e) => work.ShowDropDown();
            box.AddView(work);

            var scroll = new ScrollView(a);
            scroll.AddView(box);
            var b = new AlertDialog.Builder(a)
                .SetTitle(isNew ? Tr.T("New implement") : i.Name)
                .SetView(scroll)
                .SetPositiveButton(isNew ? Tr.T("Save and use") : Tr.T("Save"), (s, e) =>
                {
                    i.Name = name.Text;
                    i.Width = Number(width, i.Width);
                    i.Overlap = Number(overlap, 0);
                    i.Behind = Number(behind, 0, signed: true);
                    i.Right = Number(right, 0, signed: true);
                    string h = (headland.Text ?? "").Trim();
                    i.HeadlandWidth = h.Length == 0 ? (double?)null : Number(headland, 0);
                    i.WorkType = work.Text;
                    Implement saved = AppState.Implements.Save(i);
                    // A new one goes on the tractor; an edited one is applied if it is the one in use.
                    if (isNew || AppState.Implements.Active?.Id == saved.Id) AppState.UseImplement(saved);
                    changed?.Invoke();
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { });
            if (!isNew && AppState.Implements.Implements.Count > 1)
                b.SetNeutralButton(Tr.T("Delete"), (s, e) => new AlertDialog.Builder(a)
                    .SetTitle(Tr.F("Delete {0}?", i.Name))
                    .SetPositiveButton(Tr.T("Delete"), (s2, e2) =>
                    {
                        bool wasActive = AppState.Implements.Active?.Id == i.Id;
                        AppState.Implements.Delete(i.Id);
                        if (wasActive) AppState.UseImplement(AppState.Implements.Active);
                        changed?.Invoke();
                    })
                    .SetNegativeButton(Tr.T("Cancel"), (s2, e2) => { })
                    .Show());
            b.Show();
        }
    }
}
