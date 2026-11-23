# AgOpenGPS Android

An Android tablet port of [AgOpenGPS](https://github.com/agopengps-official/AgOpenGPS), the open
source GPS guidance and section control software for agriculture.

This repository holds only the Android app. The Windows (WinForms) application it started from was
removed. It is in this repository's git history, and maintained upstream at
[agopengps-official/AgOpenGPS](https://github.com/agopengps-official/AgOpenGPS).

## Status

Working on the tablet: RTK receiver over **Bluetooth** (ESP32 or Raspberry Pi bridge), NTRIP
corrections sent back over the same link, straight and curved guidance lines with passes and nudge,
lightbar, fields with boundaries (driven or by corners) and headland, worked area and statistics,
job log, satellite map (offline cache, tilt and rotate), simulator, English and Croatian.
Not tested on a tractor yet.

## Branches

| Branch | What it is |
|---|---|
| `develop` | **Navigation only**: guidance, fields, headland, worked area, map. No steering hardware needed. |
| `v2-autosteer` | **Navigation and AutoSteer**: everything in `develop`, plus steering with an ESP32 steer module (motor on the steering wheel) and automatic turns at the headland. |

Changes to navigation go into both branches.

## Contents

| Folder | What it is |
|---|---|
| [`SourceCode/Android/`](SourceCode/Android/README.md) | The Android app, its platform-neutral core library and tests. **Start here.** |
| [`SourceCode/ESP32/`](SourceCode/ESP32/README.md) | Bluetooth bridge firmware for an ESP32 in the tractor (low-cost alternative to the Pi). |
| [`SourceCode/Android/pi/`](SourceCode/Android/pi/README.md) | Bluetooth bridge for a Raspberry Pi next to the receiver. |
| `SourceCode/AgLibrary.Ntrip/` | The NTRIP client from AgIO, used by the app. |

## User guide

Short instructions for the tablet: [English](docs/user-guide.en.md) · [Hrvatski](docs/user-guide.hr.md).
The same text is in the app under **Help / Pomoć**.

## Getting the app

Every push builds the APK on GitHub Actions (*Android* workflow): open the latest run under
**Actions** and download the `AgOpenGPS-Android` artifact. To build it yourself, see
[SourceCode/Android/README.md](SourceCode/Android/README.md).

## License

Apache License 2.0, like AgOpenGPS. See [LICENSE](LICENSE).
