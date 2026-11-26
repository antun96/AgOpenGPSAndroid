# App icon

The launcher icon: a green tractor with a GNSS satellite, in the colours of the loading screen.

- `make_icon.py` writes the three SVGs on the 108 x 108 adaptive icon canvas: `bg.svg` (sky and field),
  `fg.svg` (tractor and satellite, inside the safe zone), `full.svg` (both).
- `render.js` renders them with Chromium (Playwright) into the Android sizes, in `out/mipmap-*`:
  `appicon_background.png` and `appicon_foreground.png` (adaptive icon, Android 8+) and `appicon.png`
  (older Android), plus `preview.png`.

```bash
python3 make_icon.py
```

```bash
node render.js "$PWD"
```

Then copy `out/mipmap-*/*.png` to `SourceCode/Android/AgOpenGPS.Android/Resources/mipmap-*/`.

The loading screen picture is `SourceCode/Android/AgOpenGPS.Android/Resources/drawable-nodpi/splash.webp`.
