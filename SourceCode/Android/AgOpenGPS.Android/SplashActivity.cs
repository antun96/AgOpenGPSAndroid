using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace AgOpenGPS.Droid
{
    /// <summary>
    /// The loading screen: the picture over the whole screen and a thin progress line, for a moment
    /// when the app starts, then the driving screen. When the app is still running (tapping the icon
    /// to come back to it), it goes straight on without showing the picture again.
    /// </summary>
    [Activity(Label = "@string/app_name", MainLauncher = true, NoHistory = true,
        Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
    public class SplashActivity : Activity
    {
        private const int ShowMs = 1500;
        private static bool _shownThisRun; // once per app process
        private readonly Handler _ui = new Handler(Looper.MainLooper);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            if (_shownThisRun)
            {
                GoOn();
                return;
            }
            _shownThisRun = true;

            var root = new FrameLayout(this);
            root.SetBackgroundColor(Color.ParseColor("#0B1A3D"));
            var picture = new CoverImageView(this); // picks the landscape or portrait picture by its own shape
            root.AddView(picture, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // A thin yellow line that fills while the app starts.
            float d = Resources.DisplayMetrics.Density;
            var track = new FrameLayout(this);
            track.SetBackgroundColor(Color.Argb(70, 255, 255, 255));
            var fill = new View(this);
            fill.SetBackgroundColor(Color.ParseColor("#FFDE00"));
            track.AddView(fill, new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent));
            root.AddView(track, new FrameLayout.LayoutParams((int)(280 * d), (int)(5 * d), GravityFlags.Bottom | GravityFlags.Left)
            { LeftMargin = (int)(40 * d), BottomMargin = (int)(32 * d) });
            SetContentView(root);

            DateTime start = DateTime.UtcNow;
            void Tick()
            {
                double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ShowMs);
                var lp = (FrameLayout.LayoutParams)fill.LayoutParameters;
                lp.Width = (int)(280 * d * t);
                fill.LayoutParameters = lp;
                if (t < 1) _ui.PostDelayed(Tick, 30);
                else GoOn();
            }
            _ui.Post(Tick);
        }

        private void GoOn()
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            StartActivity(intent);
            OverridePendingTransition(global::Android.Resource.Animation.FadeIn, global::Android.Resource.Animation.FadeOut);
            Finish();
        }

        protected override void OnDestroy()
        {
            _ui.RemoveCallbacksAndMessages(null);
            base.OnDestroy();
        }

        public override void OnBackPressed()
        {
            // Back on the loading screen does nothing; it is gone in a moment anyway.
        }

        /// <summary>
        /// Fills the screen with the picture without distorting it. What does not fit is cut from the
        /// right (or evenly top and bottom), so the title in the top left corner is always complete.
        /// It chooses between the landscape and the portrait picture by its own measured size (taller
        /// than wide = portrait), so it does not depend on what the system says about the orientation,
        /// and turning the tablet while it shows swaps the picture too.
        /// </summary>
        private sealed class CoverImageView : ImageView
        {
            private int _shown = -1;

            public CoverImageView(Context c) : base(c) { SetScaleType(ScaleType.Matrix); }

            protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
            {
                base.OnLayout(changed, left, top, right, bottom);
                if (Width == 0 || Height == 0) return;
                int wanted = Height > Width ? Resource.Drawable.splash_portrait : Resource.Drawable.splash_landscape;
                if (wanted != _shown)
                {
                    _shown = wanted;
                    SetImageResource(wanted);
                }
                if (Drawable == null) return;
                float iw = Drawable.IntrinsicWidth, ih = Drawable.IntrinsicHeight;
                float scale = Math.Max(Width / iw, Height / ih);
                float dy = (Height - ih * scale) / 2f; // centre vertically; horizontally keep the left edge
                var m = new Matrix();
                m.SetScale(scale, scale);
                m.PostTranslate(0, dy);
                ImageMatrix = m;
            }
        }
    }
}
