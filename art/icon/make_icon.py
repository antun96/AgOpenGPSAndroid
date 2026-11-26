# Builds the app icon (108x108 adaptive canvas) as three SVGs: background, foreground, full.
import math

def rot(points, cx, cy, deg):
    a = math.radians(deg)
    return [(cx + (x-cx)*math.cos(a) - (y-cy)*math.sin(a), cy + (x-cx)*math.sin(a) + (y-cy)*math.cos(a)) for x, y in points]

def poly(points, fill):
    return '<polygon points="%s" fill="%s"/>' % (" ".join("%.2f,%.2f" % p for p in points), fill)

background = '''
  <defs>
    <linearGradient id="sky" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#0B1A3D"/><stop offset="0.45" stop-color="#2B3A86"/>
      <stop offset="0.66" stop-color="#7A4E9A"/><stop offset="0.74" stop-color="#E8834A"/>
    </linearGradient>
    <linearGradient id="field" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#6B4A2E"/><stop offset="1" stop-color="#3A2614"/>
    </linearGradient>
    <radialGradient id="sun" cx="0.82" cy="0.68" r="0.3">
      <stop offset="0" stop-color="#FFD27A" stop-opacity="0.9"/><stop offset="1" stop-color="#FF8A3C" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <rect width="108" height="108" fill="url(#sky)"/>
  <rect width="108" height="108" fill="url(#sun)"/>
  <circle cx="30" cy="24" r="0.8" fill="#FFFFFF" opacity="0.8"/>
  <circle cx="46" cy="17" r="0.6" fill="#FFFFFF" opacity="0.7"/>
  <circle cx="22" cy="40" r="0.6" fill="#FFFFFF" opacity="0.5"/>
  <path d="M0,74 L108,74 L108,108 L0,108 Z" fill="url(#field)"/>
  <path d="M0,74 Q20,71 40,73 Q60,70 80,72.5 Q95,71 108,73 L108,75 L0,75 Z" fill="#1C2B24"/>
''' + "".join(
    # guidance lines across the field, glowing green as in the loading screen
    '<path d="M54,74 L%.1f,108" stroke="#B6FF3C" stroke-width="%.2f" opacity="%.2f"/>' % (54 + k*30, 0.9 + abs(k)*0.25, 0.85 if k in (-2, 2) else 0.0)
    for k in range(-3, 4)) + "".join(
    '<path d="M54,74 L%.1f,108" stroke="#2A1A0C" stroke-width="0.6" opacity="0.5"/>' % (54 + k*9)
    for k in range(-9, 10))

# Satellite: a body with two solar panels on a diagonal, and signal arcs towards the tractor.
sx, sy = 64, 33
body = rot([(sx-3.2, sy-3.2), (sx+3.2, sy-3.2), (sx+3.2, sy+3.2), (sx-3.2, sy+3.2)], sx, sy, 45)
panel1 = rot([(sx+4.2, sy-2.6), (sx+12.5, sy-2.6), (sx+12.5, sy+2.6), (sx+4.2, sy+2.6)], sx, sy, -45)
panel2 = rot([(sx-12.5, sy-2.6), (sx-4.2, sy-2.6), (sx-4.2, sy+2.6), (sx-12.5, sy+2.6)], sx, sy, -45)
def panel_lines(p):
    # two lines across each panel (cells)
    (x0,y0),(x1,y1),(x2,y2),(x3,y3) = p
    out = ""
    for t in (1/3, 2/3):
        ax, ay = x0+(x1-x0)*t, y0+(y1-y0)*t
        bx, by = x3+(x2-x3)*t, y3+(y2-y3)*t
        out += '<line x1="%.2f" y1="%.2f" x2="%.2f" y2="%.2f" stroke="#0D47A1" stroke-width="0.6"/>' % (ax, ay, bx, by)
    return out
arcs = ""
for i, r in enumerate((5.5, 9.5, 13.5)):
    # quarter arcs opening towards the lower left (the tractor)
    a0, a1 = math.radians(100), math.radians(170)
    x0, y0 = sx + r*math.cos(a0), sy + r*math.sin(a0)
    x1, y1 = sx + r*math.cos(a1), sy + r*math.sin(a1)
    arcs += '<path d="M%.2f,%.2f A%.2f,%.2f 0 0 1 %.2f,%.2f" fill="none" stroke="#FFFFFF" stroke-width="1.7" stroke-linecap="round" opacity="%.2f"/>' % (x0, y0, r, r, x1, y1, 1.0 - i*0.22)

satellite = (poly(panel1, "#64B5F6") + panel_lines(panel1) + poly(panel2, "#64B5F6") + panel_lines(panel2)
             + poly(body, "#ECEFF1")
             + '<circle cx="%.2f" cy="%.2f" r="1.1" fill="#90A4AE"/>' % (sx, sy) + arcs)

# Tractor, side view, facing right.
tractor = '''
  <ellipse cx="55" cy="84" rx="26" ry="2.2" fill="#000000" opacity="0.28"/>
  <!-- long hood, sloping to the grille -->
  <path d="M46,55 L71,55 Q76,55 77.5,58.5 L79.5,64 L79.5,69 L46,69 Z" fill="#367C2B"/>
  <path d="M76.6,57.6 L79.5,64 L79.5,68.5 L77,68.5 Z" fill="#1E1E1E"/>
  <rect x="46" y="60.2" width="31" height="1.4" fill="#1E1E1E"/>
  <rect x="47" y="56.4" width="22" height="1" fill="#5FA046" opacity="0.8"/>
  <!-- exhaust and air intake -->
  <rect x="63" y="44.5" width="2.2" height="11" rx="0.8" fill="#2B2B2B"/>
  <rect x="58.5" y="49.5" width="2.6" height="6" rx="0.8" fill="#2B2B2B"/>
  <!-- tall cab with a rounded roof -->
  <path d="M33,40 L50.5,40 L53,58 L33,58 Z" fill="#367C2B"/>
  <path d="M35.2,42.8 L48.8,42.8 L50.6,56.2 L35.2,56.2 Z" fill="#A7D8F0"/>
  <line x1="42" y1="42.8" x2="42.6" y2="56.2" stroke="#2B2B2B" stroke-width="0.9"/>
  <path d="M36.2,44 L40.5,44 L37.2,51.5 L36.2,51.5 Z" fill="#FFFFFF" opacity="0.6"/>
  <path d="M30.5,40.2 Q31,36.2 36,36 L49,36 Q54,36.2 54.5,40.2 Z" fill="#F4F4F4"/>
  <!-- chassis -->
  <rect x="33" y="58" width="46.5" height="11" fill="#367C2B"/>
  <rect x="33" y="67" width="46.5" height="2.6" fill="#1E1E1E"/>
  <!-- rear wheel: yellow rim -->
  <circle cx="41" cy="70" r="13" fill="#1E1E1E"/>
  <circle cx="41" cy="70" r="11" fill="none" stroke="#333333" stroke-width="2.2" stroke-dasharray="2.4,1.6"/>
  <circle cx="41" cy="70" r="6.6" fill="#FFDE00"/>
  <circle cx="41" cy="70" r="2.4" fill="#C9A800"/>
  <!-- rear fender -->
  <path d="M26.5,66 A14.6,14.6 0 0 1 55.5,66" fill="none" stroke="#2A6221" stroke-width="2.6" stroke-linecap="round"/>
  <!-- front wheel (four-wheel drive: large) and its fender -->
  <circle cx="70.5" cy="74" r="9.2" fill="#1E1E1E"/>
  <circle cx="70.5" cy="74" r="7.6" fill="none" stroke="#333333" stroke-width="1.6" stroke-dasharray="1.8,1.3"/>
  <circle cx="70.5" cy="74" r="4.6" fill="#FFDE00"/>
  <circle cx="70.5" cy="74" r="1.6" fill="#C9A800"/>
  <path d="M60.5,70.5 A10.4,10.4 0 0 1 80.5,70.5" fill="none" stroke="#2A6221" stroke-width="2" stroke-linecap="round"/>
'''

def svg(content):
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108" width="108" height="108">%s</svg>\n' % content

open("bg.svg", "w").write(svg(background))
fg = '<g transform="translate(54,56) scale(0.88) translate(-54,-56)">' + satellite + tractor + '</g>'
open("fg.svg", "w").write(svg(fg))
open("full.svg", "w").write(svg(background + fg))
