// Renders the icon SVGs to PNG with Chromium: the Android sizes and a preview sheet.
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const dir = process.argv[2];
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  const svg = n => fs.readFileSync(path.join(dir, n), 'utf8');
  async function png(svgText, size, out, mask) {
    const clip = mask === 'round' ? 'border-radius:50%;' : mask === 'squircle' ? 'border-radius:22%;' : '';
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<html><body style="margin:0;background:transparent">
      <div style="width:${size}px;height:${size}px;overflow:hidden;${clip}">${svgText.replace('width="108" height="108"', `width="${size}" height="${size}"`)}</div></body></html>`);
    await page.screenshot({ path: out, omitBackground: true });
  }
  const dens = { mdpi: 1, hdpi: 1.5, xhdpi: 2, xxhdpi: 3, xxxhdpi: 4 };
  fs.mkdirSync(path.join(dir, 'out'), { recursive: true });
  for (const [d, k] of Object.entries(dens)) {
    const o = path.join(dir, 'out', 'mipmap-' + d);
    fs.mkdirSync(o, { recursive: true });
    await png(svg('fg.svg'), Math.round(108 * k), path.join(o, 'appicon_foreground.png'));
    await png(svg('bg.svg'), Math.round(108 * k), path.join(o, 'appicon_background.png'));
    // Legacy icon (Android 7 and older): the middle 72 of 108, rounded square.
    const crop = svg('full.svg').replace('viewBox="0 0 108 108"', 'viewBox="18 18 72 72"');
    await png(crop, Math.round(48 * k), path.join(o, 'appicon.png'), 'squircle');
  }
  // Preview sheet: round and rounded-square masks (as launchers cut the 108 canvas to its middle 72), and small sizes.
  const crop = svg('full.svg').replace('viewBox="0 0 108 108"', 'viewBox="18 18 72 72"');
  const tile = (s, r) => `<div style="display:inline-block;margin:18px;text-align:center;color:#eee;font:14px sans-serif">
     <div style="width:${s}px;height:${s}px;overflow:hidden;border-radius:${r};box-shadow:0 2px 8px #0008">${crop.replace('width="108" height="108"', `width="${s}" height="${s}"`)}</div>
     <div style="margin-top:8px">AgOpenGPS</div></div>`;
  await page.setViewportSize({ width: 900, height: 420 });
  await page.setContent(`<html><body style="margin:0;padding:20px;background:linear-gradient(#37474F,#263238)">
     ${tile(192, '50%')}${tile(192, '22%')}${tile(96, '50%')}${tile(64, '22%')}${tile(48, '50%')}</body></html>`);
  await page.screenshot({ path: path.join(dir, 'preview.png') });
  await browser.close();
})();
