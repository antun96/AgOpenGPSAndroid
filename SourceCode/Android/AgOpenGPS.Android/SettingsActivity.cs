using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using AgLibrary.Ntrip;
using AgOpenGPS.Mobile.Core;
using AgOpenGPS.Mobile.Core.Guidance;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// All settings: receiver, implement, NTRIP, plus a diagnostics panel. Changes are saved when
    /// the screen is left; connection changes reconnect a running link.
    /// </summary>
    [Activity(Label = "Settings", Theme = "@android:style/Theme.Material.Light.NoActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
    public class SettingsActivity : Activity
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly List<string> _deviceAddresses = new List<string>();
        private string _connectionKeyAtStart;
        private bool _simulatorAtStart;
        private Handler _ui;
        private bool _refreshing;

        private Spinner _devices;
        private Spinner _language, _colours;
        private EditText _headlandWidth;
        private CheckBox _autoHeadland;
        private bool _skipSave; // after a restore: the fields on screen still hold the old values
        private static readonly string[] LanguageCodes = { Tr.System, Tr.English, Tr.Croatian };
        private CheckBox _autoConnect, _ntripEnabled, _rawLog, _recordCoverage, _showCoverage, _useSim, _showMap;
        private EditText _mapMaxZoom, _alertEdge, _alertOffLine;
        private readonly Dictionary<AlertKind, CheckBox> _alertBoxes = new Dictionary<AlertKind, CheckBox>();
        private EditText _antHeight, _antForward, _antRight, _toolBehind, _toolRight, _wheelbase, _nudgeStep, _simLat, _simLon, _mapUrl, _mapAttribution;
        private TextView _mapStatus;
        private System.Threading.CancellationTokenSource _prefetch;
        private EditText _width, _overlap, _host, _port, _mount, _user, _password, _gga;
        private TextView _workingWidth, _status, _raw;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            AppState.Init(this);
            _ui = new Handler(Looper.MainLooper);
            SetContentView(BuildLayout());
            LoadFields();
            LoadPairedDevices();
            _connectionKeyAtStart = AppState.Settings.ConnectionKey;
            _simulatorAtStart = AppState.Settings.UseSimulator;
        }

        protected override void OnResume()
        {
            base.OnResume();
            _refreshing = true;
            _ui.Post(Refresh);
        }

        protected override void OnPause()
        {
            _refreshing = false;
            if (!_skipSave) SaveFields();
            base.OnPause();
        }

        private void SaveFields()
        {
            AppSettings s = AppState.Settings;
            int index = _devices.SelectedItemPosition;
            if (index >= 0 && index < _deviceAddresses.Count) s.DeviceAddress = _deviceAddresses[index];
            s.AutoConnect = _autoConnect.Checked;
            s.ToolWidth = _width.Text.Trim();
            s.ToolOverlap = _overlap.Text.Trim();
            s.NtripEnabled = _ntripEnabled.Checked;
            s.NtripHost = _host.Text.Trim();
            s.NtripPort = _port.Text.Trim();
            s.NtripMount = _mount.Text.Trim();
            s.NtripUser = _user.Text.Trim();
            s.NtripPassword = _password.Text;
            s.NtripGgaSeconds = _gga.Text.Trim();
            s.ShowRawLog = _rawLog.Checked;
            s.AntennaHeight = _antHeight.Text.Trim();
            s.AntennaForward = _antForward.Text.Trim();
            s.AntennaRight = _antRight.Text.Trim();
            s.ToolBehind = _toolBehind.Text.Trim();
            s.ToolRight = _toolRight.Text.Trim();
            s.Wheelbase = _wheelbase.Text.Trim();
            s.NudgeStepCm = _nudgeStep.Text.Trim();
            s.UseSimulator = _useSim.Checked;
            if (!s.UseSimulator && (_simLat.Text.Trim().Length > 0 || _simLon.Text.Trim().Length > 0)
                && (_simLat.Text.Trim() != s.SimLatitude || _simLon.Text.Trim() != s.SimLongitude))
                Toast.MakeText(this, Tr.T("The start position is used only with 'Use the simulator' ticked"), ToastLength.Long).Show();
            s.SimLatitude = _simLat.Text.Trim();
            s.SimLongitude = _simLon.Text.Trim();
            s.ShowMap = _showMap.Checked;
            s.MapUrl = string.IsNullOrWhiteSpace(_mapUrl.Text) ? MapTiles.DefaultUrl : _mapUrl.Text.Trim();
            s.MapAttribution = _mapAttribution.Text.Trim();
            s.MapMaxZoom = _mapMaxZoom.Text.Trim();
            s.Alerts.Clear();
            foreach (var kv in _alertBoxes) if (kv.Value.Checked) s.Alerts.Add(kv.Key);
            s.AlertEdgeMeters = _alertEdge.Text.Trim();
            s.AlertOffLineCm = _alertOffLine.Text.Trim();
            s.HeadlandWidth = _headlandWidth.Text.Trim();
            s.AutoHeadland = _autoHeadland.Checked;
            s.DayColours = _colours.SelectedItemPosition == 1;
            if (s.RecordCoverage && !_recordCoverage.Checked) AppState.SetWorking(false); // switched off: stop and save
            s.RecordCoverage = _recordCoverage.Checked;
            s.ShowCoverage = _showCoverage.Checked;
            s.Save();
            AppState.Guidance.PassWidth = s.WorkingWidthMeters;
            AppState.Guidance.ToolWidth = s.ToolWidthMeters;
            AppState.Guidance.CoverageEnabled = s.RecordCoverage;
            AppState.Guidance.Vehicle = s.BuildVehicleGeometry();
            AppState.ApplySettings();
            AppState.Map.UrlTemplate = s.MapUrl;

            string key = s.ConnectionKey;
            if (key != _connectionKeyAtStart)
            {
                bool simChanged = s.UseSimulator != _simulatorAtStart;
                _connectionKeyAtStart = key;
                _simulatorAtStart = s.UseSimulator;
                if (AppState.IsConnected) Task.Run(AppState.Reconnect);
                // Ticking the simulator means "drive it now": connect without a trip to the Connect button.
                else if (simChanged && s.UseSimulator && HasBluetoothPermission()) AppState.Connect();
            }
        }

        private void LoadFields()
        {
            AppSettings s = AppState.Settings;
            _autoConnect.Checked = s.AutoConnect;
            _width.Text = s.ToolWidth;
            _overlap.Text = s.ToolOverlap;
            _ntripEnabled.Checked = s.NtripEnabled;
            _host.Text = s.NtripHost;
            _port.Text = s.NtripPort;
            _mount.Text = s.NtripMount;
            _user.Text = s.NtripUser;
            _password.Text = s.NtripPassword;
            _gga.Text = s.NtripGgaSeconds;
            _rawLog.Checked = s.ShowRawLog;
            _recordCoverage.Checked = s.RecordCoverage;
            _showCoverage.Checked = s.ShowCoverage;
            _showCoverage.Enabled = s.RecordCoverage;
            _antHeight.Text = s.AntennaHeight;
            _antForward.Text = s.AntennaForward;
            _antRight.Text = s.AntennaRight;
            _toolBehind.Text = s.ToolBehind;
            _toolRight.Text = s.ToolRight;
            _wheelbase.Text = s.Wheelbase;
            _nudgeStep.Text = s.NudgeStepCm;
            _useSim.Checked = s.UseSimulator;
            _simLat.Text = s.SimLatitude;
            _simLon.Text = s.SimLongitude;
            _showMap.Checked = s.ShowMap;
            _mapUrl.Text = s.MapUrl;
            _mapAttribution.Text = s.MapAttribution;
            _mapMaxZoom.Text = s.MapMaxZoom;
            foreach (var kv in _alertBoxes) kv.Value.Checked = s.Alerts.Contains(kv.Key);
            _alertEdge.Text = s.AlertEdgeMeters;
            _alertOffLine.Text = s.AlertOffLineCm;
            _headlandWidth.Text = s.HeadlandWidth;
            _autoHeadland.Checked = s.AutoHeadland;
            Task.Run(() =>
            {
                long bytes = AppState.Map.DiskBytes();
                RunOnUiThread(() => _mapStatus.Text = string.Format(Inv, Tr.T("Saved on the tablet: {0:0.0} MB"), bytes / 1048576.0));
            });
            UpdateWorkingWidth();
        }

        private void UpdateWorkingWidth()
        {
            double w = AppSettings.ParseMeters(_width.Text, double.NaN);
            double o = AppSettings.ParseMeters(_overlap.Text, double.NaN);
            _workingWidth.Text = double.IsNaN(w) || double.IsNaN(o)
                ? Tr.T("Enter the width in metres, e.g. 6.0")
                : string.Format(Inv, Tr.T("Distance between passes: {0:0.00} m"), System.Math.Max(0.1, w - o));
        }

        private void LoadPairedDevices()
        {
            _deviceAddresses.Clear();
            var names = new List<string>();
            BluetoothAdapter adapter = AppState.Adapter;

            if (adapter == null) names.Add(Tr.T("This tablet has no Bluetooth"));
            else if (!adapter.IsEnabled) names.Add(Tr.T("Bluetooth is off - turn it on and press Refresh"));
            else
            {
                try
                {
                    foreach (BluetoothDevice d in adapter.BondedDevices.OrderBy(d => d.Name ?? ""))
                    {
                        _deviceAddresses.Add(d.Address);
                        names.Add((string.IsNullOrEmpty(d.Name) ? Tr.T("(no name)") : d.Name) + "  " + d.Address);
                    }
                }
                catch (Java.Lang.SecurityException)
                {
                    names.Add(Tr.T("Allow 'Nearby devices' for AgOpenGPS in Android settings"));
                }
                if (names.Count == 0) names.Add(Tr.T("No paired devices - press Pair..."));
            }

            var a = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, names);
            a.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _devices.Adapter = a;
            int selected = _deviceAddresses.IndexOf(AppState.Settings.DeviceAddress);
            if (selected >= 0) _devices.SetSelection(selected);
        }

        // The background service (connectedDevice) needs the Bluetooth permission, also for the simulator.
        private bool HasBluetoothPermission()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return true;
            return CheckSelfPermission(global::Android.Manifest.Permission.BluetoothConnect) == Permission.Granted;
        }

        // ---------------------------------------------------------------- diagnostics, 2 Hz

        private void Refresh()
        {
            if (!_refreshing) return;
            try { RenderStatus(); }
            finally { _ui.PostDelayed(Refresh, 500); }
        }

        private void RenderStatus()
        {
            LinkSnapshot snap = AppState.Link?.GetSnapshot();
            var sb = new StringBuilder();
            if (snap == null)
            {
                sb.Append(Tr.T("Bluetooth: not connected"));
            }
            else
            {
                sb.Append(Tr.F("{0}: {1} - {2}", AppState.Simulator != null ? "Simulator" : "Bluetooth", Tr.T(snap.State.ToString()), Tr.T(snap.Message)));
                if (snap.Connects > 1) sb.Append(Tr.F("  (reconnects: {0})", snap.Connects - 1));
                SessionStats st = snap.Session;
                if (st != null)
                {
                    GnssFix f = st.Latest;
                    sb.Append('\n').AppendFormat(Inv, Tr.T("Data: {0:0.0} Hz, {1}"), st.RateHz,
                        f == null ? Tr.T("no position yet") : f.Source == FixSource.Pgn ? Tr.T("AgOpenGPS PGN (bridge)") : Tr.T("NMEA (receiver)"));
                    if (f != null && f.HasPosition)
                        sb.Append('\n').AppendFormat(Inv, Tr.T("{0}  {1:0.0000000} {2:0.0000000}  sats {3}  HDOP {4:0.00}  age {5:0.0} s"),
                            Tr.T(GnssFix.FixQualityName(f.FixQuality)), f.Latitude, f.Longitude, f.Satellites, f.Hdop, f.AgeSeconds);
                    sb.Append('\n').AppendFormat(Inv,
                        Tr.T("In {0:N0} B   out {1:N0} B   NMEA ok {2:N0} / bad {3:N0}   PGN {4:N0}   skipped {5:N0} B"),
                        st.BytesIn, st.BytesOut, st.Sentences, st.BadSentences, st.PgnFrames, st.SkippedBytes);
                }
                NtripStatus n = snap.Ntrip;
                sb.Append('\n').Append("NTRIP: ");
                if (n == null) sb.Append(AppState.Settings.NtripEnabled ? Tr.T("not running (check settings)") : Tr.T("off"));
                else
                {
                    sb.Append(n.Headline);
                    if (n.Frames > 0) sb.AppendFormat(Inv, Tr.T(", {0} frames, last {1:0.0} s ago, {2:N0} B to receiver"), n.Frames, n.SecondsSinceLastFrame, snap.NtripBytesSent);
                    else if (!string.IsNullOrEmpty(n.Message)) sb.Append(" - ").Append(n.Message);
                }
            }
            _status.Text = sb.ToString();

            _raw.Visibility = _rawLog.Checked ? ViewStates.Visible : ViewStates.Gone;
            if (_rawLog.Checked) _raw.Text = string.Join("\n", AppState.RawLog.ToArray());
        }

        private void ChangeLanguage(string code)
        {
            if (code == AppState.Settings.Language) return; // the spinner reports its first selection too
            SaveFields();
            AppState.Settings.Language = code;
            AppState.Settings.Save();
            int before = Tr.Version;
            Tr.SetLanguage(code);
            if (Tr.Version != before) Recreate(); // rebuild this screen; the driving screen follows on return
        }

        // ---------------------------------------------------------------- backup

        private void SaveBackup()
        {
            SaveFields();
            DataBackup.Save(this);
        }

        private void PickBackup() => DataBackup.Pick(this);

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (FileSaver.HandleResult(this, requestCode, resultCode, data)) return;
            DataBackup.HandleResult(this, requestCode, resultCode, data, () =>
            {
                _skipSave = true; // the fields on screen still hold the old values
                Recreate();
            });
        }

        // ---------------------------------------------------------------- map download

        /// <summary>Downloads zoom 14 to 19 for the open field's boundary (or 500 m around the tractor), plus 100 m.</summary>
        private void DownloadFieldMap()
        {
            if (_prefetch != null)
            {
                _prefetch.Cancel();
                return;
            }
            SaveFields();

            double south, west, north, east;
            Field field = AppState.Fields.Active;
            GnssFix fix = AppState.Guidance.GetSnapshot().Fix;
            if (field != null && field.HasBoundary)
            {
                south = field.Boundary.Min(p => p.Latitude); north = field.Boundary.Max(p => p.Latitude);
                west = field.Boundary.Min(p => p.Longitude); east = field.Boundary.Max(p => p.Longitude);
            }
            else if (fix != null && fix.HasPosition)
            {
                south = north = fix.Latitude;
                west = east = fix.Longitude;
                double m = 400;
                south -= m / 111132; north += m / 111132;
                double dl = m / (111320 * Math.Cos(fix.Latitude * Math.PI / 180));
                west -= dl; east += dl;
            }
            else
            {
                Toast.MakeText(this, Tr.T("Open a field with a boundary, or connect to get a position first"), ToastLength.Long).Show();
                return;
            }
            double margin = 100;
            south -= margin / 111132; north += margin / 111132;
            double dm = margin / (111320 * Math.Cos(south * Math.PI / 180));
            west -= dm; east += dm;

            var tiles = new List<TileId>();
            for (int z = 14; z <= AppState.Settings.MapMaxZoomLevel; z++) tiles.AddRange(MapTiles.TilesFor(south, west, north, east, z));
            if (tiles.Count > 3000)
            {
                Toast.MakeText(this, Tr.F("That area is too large ({0} tiles)", tiles.Count), ToastLength.Long).Show();
                return;
            }

            new AlertDialog.Builder(this)
                .SetTitle(Tr.T("Download the map?"))
                .SetMessage(string.Format(Inv, Tr.T("{0} map squares, about {1:0} MB. Best over Wi-Fi."), tiles.Count, tiles.Count * 0.02))
                .SetPositiveButton(Tr.T("Download"), (s, e) => StartPrefetch(tiles))
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        private void StartPrefetch(List<TileId> tiles)
        {
            _prefetch = new System.Threading.CancellationTokenSource();
            var ct = _prefetch.Token;
            _mapStatus.Text = Tr.T("Downloading... (press the button again to stop)");
            Task.Run(async () =>
            {
                int failed = await AppState.Map.PrefetchAsync(tiles,
                    (done, total) => RunOnUiThread(() => _mapStatus.Text = string.Format(Inv, Tr.T("Downloading {0} / {1}"), done, total)), ct);
                long bytes = AppState.Map.DiskBytes();
                RunOnUiThread(() =>
                {
                    _prefetch = null;
                    _mapStatus.Text = ct.IsCancellationRequested ? Tr.T("Download stopped")
                        : failed == 0 ? string.Format(Inv, Tr.T("Map ready. Saved on the tablet: {0:0.0} MB"), bytes / 1048576.0)
                        : string.Format(Inv, Tr.T("{0} squares failed (no internet?). Try again later."), failed);
                });
            });
        }

        private void ClearMapCache()
        {
            new AlertDialog.Builder(this)
                .SetTitle(Tr.T("Delete the saved map?"))
                .SetMessage(Tr.T("It is downloaded again when needed. Do this only when you want newer imagery."))
                .SetPositiveButton(Tr.T("Delete"), (s, e) =>
                {
                    AppState.Map.ClearDisk();
                    _mapStatus.Text = Tr.T("Saved on the tablet: 0.0 MB");
                })
                .SetNegativeButton(Tr.T("Cancel"), (s, e) => { })
                .Show();
        }

        // ---------------------------------------------------------------- layout

        private int Dp(float v) => Ui.Dp(this, v);

        // Sections of the screen: the chosen one is kept while the app runs (also across a language change).
        private static int _section;
        private readonly List<(string Name, LinearLayout Page, Button Button)> _sections = new List<(string, LinearLayout, Button)>();
        private ScrollView _pageScroll;

        private void ShowSection(int index)
        {
            _section = index;
            for (int i = 0; i < _sections.Count; i++)
            {
                var (_, pg, b) = _sections[i];
                bool on = i == index;
                pg.Visibility = on ? ViewStates.Visible : ViewStates.Gone;
                b.SetBackgroundColor(Color.ParseColor(on ? "#1565C0" : "#FAFAFA"));
                b.SetTextColor(on ? Color.White : Color.ParseColor("#212121"));
            }
            _pageScroll?.ScrollTo(0, 0);
        }

        private View BuildLayout()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(Color.ParseColor("#F2F2F2"));

            // Title bar.
            var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            bar.SetBackgroundColor(Color.ParseColor("#37474F"));
            bar.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
            bar.SetGravity(GravityFlags.CenterVertical);
            bar.AddView(Ui.Button(this, Tr.T("< Back"), Finish, 48));
            TextView title = Ui.Text(this, 22, true, "#FFFFFF");
            title.Text = Tr.T("  Settings");
            bar.AddView(title);
            root.AddView(bar);

            // Settings in sections: a menu on the left, the chosen section on the right.
            var body = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var menu = new LinearLayout(this) { Orientation = Orientation.Vertical };
            menu.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
            var menuScroll = new ScrollView(this);
            menuScroll.SetBackgroundColor(Color.ParseColor("#E3E7EA"));
            menuScroll.AddView(menu);
            body.AddView(menuScroll, new LinearLayout.LayoutParams(Dp(220), ViewGroup.LayoutParams.MatchParent));
            var pages = new LinearLayout(this) { Orientation = Orientation.Vertical };
            pages.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
            _pageScroll = new ScrollView(this);
            _pageScroll.AddView(pages);
            body.AddView(_pageScroll, Ui.Weight(1, ViewGroup.LayoutParams.MatchParent));
            _sections.Clear();

            // A section's page; the same name again continues the same page.
            LinearLayout Section(string name)
            {
                foreach (var sec in _sections) if (sec.Name == name) return sec.Page;
                LinearLayout pg = Ui.Card(this);
                pg.AddView(Ui.Heading(this, name));
                pages.AddView(pg, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                int index = _sections.Count;
                Button b = Ui.Button(this, name, () => ShowSection(index), 52);
                b.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
                menu.AddView(b, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });
                _sections.Add((name, pg, b));
                return pg;
            }
            // The menu order.
            foreach (string name in new[] { "General", "Receiver", "NTRIP", "Implement", "Vehicle", "Headland", "Map", "Sound alerts", "Simulator", "Backup", "Diagnostics" })
                Section(Tr.T(name));
            LinearLayout page;

            // Language first, labelled in both languages so it can be found in either.
            page = Section(Tr.T("General"));
            page.AddView(Ui.Heading(this, "Language / Jezik"));
            _language = new Spinner(this);
            var langAdapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem,
                new[] { "System / Sustav", "English", "Hrvatski" });
            langAdapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _language.Adapter = langAdapter;
            _language.SetSelection(Math.Max(0, Array.IndexOf(LanguageCodes, AppState.Settings.Language)), false);
            _language.ItemSelected += (s, e) => ChangeLanguage(LanguageCodes[e.Position]);
            page.AddView(_language);

            page.AddView(Ui.Heading(this, Tr.T("Colours")));
            _colours = new Spinner(this);
            var colourAdapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem,
                new[] { Tr.T("Night (dark)"), Tr.T("Day (light, for bright sun)") });
            colourAdapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _colours.Adapter = colourAdapter;
            _colours.SetSelection(AppState.Settings.DayColours ? 1 : 0, false);
            page.AddView(_colours);

            page = Section(Tr.T("Receiver"));
            _devices = new Spinner(this);
            page.AddView(_devices);
            var devButtons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            devButtons.AddView(Ui.Button(this, Tr.T("Refresh"), LoadPairedDevices), Ui.Weight(1));
            devButtons.AddView(Ui.Button(this, Tr.T("Pair..."), () => StartActivity(new Intent(global::Android.Provider.Settings.ActionBluetoothSettings))), Ui.Weight(1));
            page.AddView(devButtons);
            _autoConnect = new CheckBox(this) { Text = Tr.T("Connect automatically when the app starts") };
            page.AddView(_autoConnect);

            page = Section(Tr.T("Implement"));
            _width = Ui.Field(this, page, Tr.T("Implement width (m)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            page.AddView(Ui.Hint(this, Tr.T("The real full width of the implement. The worked area always uses the full width.")));
            _overlap = Ui.Field(this, page, Tr.T("Overlap (m)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            page.AddView(Ui.Hint(this, Tr.T("Only for the spacing between passes: passes are implement width minus overlap apart.")));
            _workingWidth = Ui.Hint(this, "");
            page.AddView(_workingWidth);
            _width.TextChanged += (s, e) => UpdateWorkingWidth();
            _overlap.TextChanged += (s, e) => UpdateWorkingWidth();
            page.AddView(Ui.Hint(this, Tr.T("Guidance is for the antenna position.")));

            page = Section(Tr.T("Vehicle"));
            page.AddView(Ui.Hint(this, Tr.T("Metres. Measure from the middle of the rear axle. Left / behind = negative.")));
            InputTypes signed = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned;
            _antHeight = Ui.Field(this, page, Tr.T("Antenna height above ground (corrects tilt on slopes; needs roll from the receiver)"), signed);
            _antForward = Ui.Field(this, page, Tr.T("Antenna ahead of the rear axle"), signed);
            _antRight = Ui.Field(this, page, Tr.T("Antenna right of the centre line"), signed);
            _toolBehind = Ui.Field(this, page, Tr.T("Implement working line behind the rear axle (front mounted = negative)"), signed);
            _toolRight = Ui.Field(this, page, Tr.T("Implement centre right of the tractor centre line"), signed);
            _wheelbase = Ui.Field(this, page, Tr.T("Wheelbase (simulator and steering)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            page.AddView(Ui.Hint(this, Tr.T("Guidance follows the implement's centre line; the worked area is painted where the implement works.")));
            _nudgeStep = Ui.Field(this, page, Tr.T("Nudge step (cm)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);

            page = Section(Tr.T("Headland"));
            _headlandWidth = Ui.Field(this, page, Tr.T("Headland width (m, 0 = no headland)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            page.AddView(Ui.Hint(this, Tr.T("A strip of this width along the field boundary, drawn as a dashed line. Usually a multiple of the implement width.")));
            _autoHeadland = new CheckBox(this) { Text = Tr.T("Work off in the headland, on again in the main part") };
            page.AddView(_autoHeadland);
            page.AddView(Ui.Hint(this, Tr.T("Only after you switched Work on yourself; switching it off by hand ends the automatic until the next Work: ON.")));

            page = Section(Tr.T("Sound alerts"));
            page.AddView(Ui.Hint(this, Tr.T("Each alert sounds once per event, on the alarm channel (also with the tablet on silent).")));
            void AlertBox(AlertKind kind, string text)
            {
                var cb = new CheckBox(this) { Text = Tr.T(text) };
                _alertBoxes[kind] = cb;
                page.AddView(cb);
            }
            AlertBox(AlertKind.FieldEdge, "Field edge ahead");
            AlertBox(AlertKind.HeadlandAhead, "Headland ahead");
            _alertEdge = Ui.Field(this, page, Tr.T("Warn this far from the edge (m)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            AlertBox(AlertKind.LeftField, "Left the field");
            AlertBox(AlertKind.RtkLost, "RTK Fix lost");
            AlertBox(AlertKind.RtkBack, "RTK Fix is back");
            AlertBox(AlertKind.CorrectionsStopped, "NTRIP corrections stopped (15 s)");
            AlertBox(AlertKind.LinkLost, "Connection to the receiver lost");
            AlertBox(AlertKind.OffLine, "Off the line");
            _alertOffLine = Ui.Field(this, page, Tr.T("Off the line after (cm, for 2 s, when moving)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal);
            page.AddView(Ui.Button(this, Tr.T("Test sound"), () =>
            {
                AppState.Sounds.Play(AlertKind.RtkLost);
                Toast.MakeText(this, Tr.T("Too quiet? Turn up the alarm volume of the tablet."), ToastLength.Long).Show();
            }));

            page = Section(Tr.T("Map"));
            page.AddView(Ui.Heading(this, Tr.T("Worked area")));
            _recordCoverage = new CheckBox(this) { Text = Tr.T("Record worked area (Work button, statistics)") };
            page.AddView(_recordCoverage);
            _showCoverage = new CheckBox(this) { Text = Tr.T("Show worked area on the map") };
            page.AddView(_showCoverage);
            _recordCoverage.CheckedChange += (s, e) => _showCoverage.Enabled = e.IsChecked;
            page.AddView(Ui.Hint(this, Tr.T("Recording uses very little; on a slow tablet hide it from the map first.")));

            page = Section(Tr.T("Diagnostics"));
            _status = Ui.Text(this, 14);
            page.AddView(_status);
            _rawLog = new CheckBox(this) { Text = Tr.T("Show raw NMEA") };
            page.AddView(_rawLog);
            _raw = Ui.Text(this, 12);
            _raw.Typeface = Typeface.Monospace;
            page.AddView(_raw);

            page = Section(Tr.T("NTRIP"));
            _ntripEnabled = new CheckBox(this) { Text = Tr.T("Send NTRIP corrections to the receiver over Bluetooth") };
            page.AddView(_ntripEnabled);
            page.AddView(Ui.Hint(this, Tr.T("Turn off if the Raspberry Pi or receiver gets corrections itself.")));
            _host = Ui.Field(this, page, Tr.T("Caster host"), InputTypes.ClassText | InputTypes.TextVariationUri);
            _port = Ui.Field(this, page, "Port", InputTypes.ClassNumber);
            _mount = Ui.Field(this, page, "Mountpoint", InputTypes.ClassText);
            _user = Ui.Field(this, page, Tr.T("User"), InputTypes.ClassText);
            _password = Ui.Field(this, page, Tr.T("Password"), InputTypes.ClassText | InputTypes.TextVariationPassword);
            _gga = Ui.Field(this, page, Tr.T("Send GGA every (s, 0 = once)"), InputTypes.ClassNumber);
            page.AddView(Ui.Hint(this, Tr.T("Changes apply when you leave this screen; a running connection reconnects.")));

            page = Section(Tr.T("Simulator"));
            _useSim = new CheckBox(this) { Text = Tr.T("Use the simulator instead of the receiver") };
            page.AddView(_useSim);
            page.AddView(Ui.Hint(this, Tr.T("A virtual tractor with RTK fix, to try everything at home. Speed and steering on the driving screen.")));
            _simLat = Ui.Field(this, page, Tr.T("Start latitude (empty = open field or last position)"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned);
            _simLon = Ui.Field(this, page, Tr.T("Start longitude"), InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned);

            page = Section(Tr.T("Map"));
            page.AddView(Ui.Heading(this, Tr.T("Satellite map")));
            _showMap = new CheckBox(this) { Text = Tr.T("Show the satellite map") };
            page.AddView(_showMap);
            page.AddView(Ui.Hint(this, Tr.T("Each map square is downloaded once and kept on the tablet; it works without internet afterwards.")));
            var mapButtons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            mapButtons.AddView(Ui.Button(this, Tr.T("Download map for the open field"), DownloadFieldMap), Ui.Weight(2));
            mapButtons.AddView(Ui.Button(this, Tr.T("Clear map cache"), ClearMapCache), Ui.Weight(1));
            page.AddView(mapButtons);
            _mapStatus = Ui.Hint(this, "");
            page.AddView(_mapStatus);
            _mapUrl = Ui.Field(this, page, Tr.T("Map source (tile URL with {z} {x} {y})"), InputTypes.ClassText | InputTypes.TextVariationUri);
            _mapAttribution = Ui.Field(this, page, Tr.T("Attribution shown on the map"), InputTypes.ClassText);
            _mapMaxZoom = Ui.Field(this, page, Tr.T("Most detailed zoom to download (10-22, default 19)"), InputTypes.ClassNumber);
            page.AddView(Ui.Hint(this, Tr.T("If close-up imagery is missing or grey in your area, try 18 or 17. Closer than this, the map is enlarged.")));
            page.AddView(Ui.Hint(this, Tr.T("Default: Esri World Imagery, free with attribution. Check the source's terms, especially for commercial use.")));

            page = Section(Tr.T("Backup"));
            page.AddView(Ui.Hint(this, Tr.T("One file with everything: all settings, fields, lines, jobs, tracks and worked areas, to move to another tablet (also under Records → Data). Contains the NTRIP password: keep it private.")));
            var backup = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            backup.AddView(Ui.Button(this, Tr.T("Save backup"), SaveBackup), Ui.Weight(1));
            backup.AddView(Ui.Button(this, Tr.T("Restore backup"), PickBackup), Ui.Weight(1));
            page.AddView(backup);

            root.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
            ShowSection(Math.Min(_section, _sections.Count - 1));
            return root;
        }
    }
}
