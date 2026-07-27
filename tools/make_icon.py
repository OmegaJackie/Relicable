"""Generate icon.png for Relicable — original art evoking the ARR Zodiac relic line.

Design language (all original vector drawing, no game assets):
  * deep midnight arcanum backdrop (the relic books / Labyrinth of the Ancients palette)
  * a twelve-node zodiac ring — the twelve Atmas / zodiac signs of the Zodiac Braves story
  * an upward relic blade with a white-hot core and gold edge — the Nexus "Light" gauge
  * faint inner arcane circle + sparks, like an active aetherial enchantment

Rendered at 4x (2048) and downscaled to 512 for clean anti-aliasing.
"""
import math
from PIL import Image, ImageDraw, ImageFilter

S = 2048                      # supersampled canvas
CX, CY = S // 2, S // 2
GOLD = (212, 175, 55)
GOLD_BRIGHT = (246, 216, 130)
GOLD_DIM = (150, 118, 40)
LIGHT = (170, 230, 255)       # Nexus light cyan
WHITE = (245, 250, 255)


def radial_gradient(size, inner, outer):
    img = Image.new("RGB", (size, size), outer)
    d = ImageDraw.Draw(img)
    steps = 240
    maxr = size * 0.72
    for i in range(steps, 0, -1):
        t = i / steps
        r = maxr * t
        col = tuple(int(o + (n - o) * (1 - t) for n, o in [(inner[c], outer[c])][0]) for c in range(3)) if False else tuple(
            int(outer[c] + (inner[c] - outer[c]) * (1 - t)) for c in range(3))
        d.ellipse([CX - r, CY - r, CX + r, CY + r], fill=col)
    return img


def glow(base, draw_fn, blur, alpha=255):
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(layer))
    layer = layer.filter(ImageFilter.GaussianBlur(blur))
    if alpha < 255:
        a = layer.getchannel("A").point(lambda v: v * alpha // 255)
        layer.putalpha(a)
    base.alpha_composite(layer)


def diamond(d, x, y, r, fill, outline=None, width=1):
    d.polygon([(x, y - r), (x + r, y), (x, y + r), (x - r, y)], fill=fill, outline=outline, width=width)


def sparkle(d, x, y, r, col):
    # 4-point star: two thin quads
    d.polygon([(x, y - r), (x + r * 0.18, y), (x, y + r), (x - r * 0.18, y)], fill=col)
    d.polygon([(x - r, y), (x, y + r * 0.18), (x + r, y), (x, y - r * 0.18)], fill=col)


# ----- background: midnight arcanum -------------------------------------------------
img = radial_gradient(S, inner=(26, 34, 66), outer=(7, 9, 20)).convert("RGBA")

# faint star field
d = ImageDraw.Draw(img)
import random
rng = random.Random(20130827)   # ARR release date as the seed, for reproducibility
for _ in range(90):
    x, y = rng.uniform(0, S), rng.uniform(0, S)
    dist = math.hypot(x - CX, y - CY)
    if dist < S * 0.30:         # keep the middle clean for the blade
        continue
    r = rng.uniform(1.5, 4.5)
    a = rng.randint(40, 110)
    d.ellipse([x - r, y - r, x + r, y + r], fill=(200, 215, 255, a))

# ----- warm halo behind everything ---------------------------------------------------
glow(img, lambda g: g.ellipse([CX - 560, CY - 560, CX + 560, CY + 560],
                              fill=(GOLD[0], GOLD[1], GOLD[2], 60)), blur=220)

# ----- zodiac ring -------------------------------------------------------------------
R_OUT = 800    # outer ring radius
R_IN = 700     # inner companion ring
ring = Image.new("RGBA", img.size, (0, 0, 0, 0))
rd = ImageDraw.Draw(ring)
rd.ellipse([CX - R_OUT, CY - R_OUT, CX + R_OUT, CY + R_OUT], outline=GOLD + (255,), width=16)
rd.ellipse([CX - R_IN, CY - R_IN, CX + R_IN, CY + R_IN], outline=GOLD_DIM + (200,), width=6)

# twelve zodiac nodes on the outer ring; the top one is brightest (the "current" atma)
R_NODE = (R_OUT + R_IN) // 2
for i in range(12):
    ang = math.radians(i * 30 - 90)
    x = CX + R_NODE * math.cos(ang)
    y = CY + R_NODE * math.sin(ang)
    if i == 0:
        diamond(rd, x, y, 46, fill=GOLD_BRIGHT + (255,))
        diamond(rd, x, y, 46, fill=None, outline=WHITE + (255,), width=6)
    else:
        diamond(rd, x, y, 34, fill=GOLD + (235,))
        diamond(rd, x, y, 18, fill=(20, 24, 44, 255))

# tick marks between nodes
for i in range(12):
    ang = math.radians(i * 30 - 75)
    x1 = CX + (R_IN + 12) * math.cos(ang)
    y1 = CY + (R_IN + 12) * math.sin(ang)
    x2 = CX + (R_OUT - 12) * math.cos(ang)
    y2 = CY + (R_OUT - 12) * math.sin(ang)
    rd.line([x1, y1, x2, y2], fill=GOLD_DIM + (160,), width=6)

# soft gold glow under the ring, then the crisp ring on top
glow(img, lambda g: g.ellipse([CX - R_OUT, CY - R_OUT, CX + R_OUT, CY + R_OUT],
                              outline=GOLD + (200,), width=40), blur=40)
img.alpha_composite(ring)

# ----- inner arcane circle -----------------------------------------------------------
R_ARC = 520
arc = Image.new("RGBA", img.size, (0, 0, 0, 0))
ad = ImageDraw.Draw(arc)
ad.ellipse([CX - R_ARC, CY - R_ARC, CX + R_ARC, CY + R_ARC], outline=GOLD_DIM + (110,), width=5)
# rotated square inscribed in it (classic arcanum geometry)
pts = []
for i in range(4):
    ang = math.radians(i * 90 - 90)
    pts.append((CX + R_ARC * math.cos(ang), CY + R_ARC * math.sin(ang)))
ad.polygon(pts, outline=GOLD_DIM + (80,), width=4)
img.alpha_composite(arc)

# ----- the relic blade ---------------------------------------------------------------
# Upward sword, centered. Proportions tuned for silhouette clarity at 64px.
TIP = CY - 660          # blade tip y
GUARD = CY + 240        # cross-guard y
POMMEL = CY + 560       # pommel center y
BW = 92                 # blade half-width at the guard

blade = Image.new("RGBA", img.size, (0, 0, 0, 0))
bd = ImageDraw.Draw(blade)

# blade silhouette: elongated hexagon
blade_pts = [
    (CX, TIP),
    (CX + BW * 0.62, TIP + 190),
    (CX + BW, GUARD),
    (CX - BW, GUARD),
    (CX - BW * 0.62, TIP + 190),
]
bd.polygon(blade_pts, fill=(232, 240, 252, 255))
# gold edge outline
bd.polygon(blade_pts, outline=GOLD + (255,), width=12)
# fuller: the glowing light channel down the middle (the Nexus gauge, full)
bd.polygon([
    (CX, TIP + 120),
    (CX + BW * 0.30, TIP + 260),
    (CX + BW * 0.30, GUARD - 30),
    (CX - BW * 0.30, GUARD - 30),
    (CX - BW * 0.30, TIP + 260),
], fill=LIGHT + (255,))

# cross-guard: wide bar with upswept tips (Curtana-like flare)
GW = 320                 # guard half-width
GT = 38                  # guard half-thickness
guard_pts = [
    (CX - GW, GUARD - GT - 110),          # left wing tip, swept up
    (CX - GW + 46, GUARD - GT - 96),
    (CX - GW + 130, GUARD - GT),          # wing meets the bar
    (CX + GW - 130, GUARD - GT),
    (CX + GW - 46, GUARD - GT - 96),
    (CX + GW, GUARD - GT - 110),          # right wing tip, swept up
    (CX + GW - 26, GUARD + GT),
    (CX - GW + 26, GUARD + GT),
]
bd.polygon(guard_pts, fill=GOLD + (255,))
bd.polygon(guard_pts, outline=GOLD_BRIGHT + (255,), width=8)
# a light gem set in the guard over the blade root
bd.ellipse([CX - 34, GUARD - GT - 6, CX + 34, GUARD + GT - 10], fill=LIGHT + (255,))
bd.ellipse([CX - 34, GUARD - GT - 6, CX + 34, GUARD + GT - 10], outline=GOLD_BRIGHT + (255,), width=6)

# grip
bd.rectangle([CX - 40, GUARD + GT, CX + 40, POMMEL - 60], fill=(58, 44, 80, 255),
             outline=GOLD_DIM + (255,), width=8)
# grip wrap lines
for i in range(1, 5):
    y = GUARD + GT + (POMMEL - 60 - GUARD - GT) * i / 5
    bd.line([CX - 40, y - 14, CX + 40, y + 14], fill=GOLD_DIM + (200,), width=7)

# pommel: gold ring with a light gem
bd.ellipse([CX - 74, POMMEL - 74, CX + 74, POMMEL + 74], fill=GOLD + (255,))
bd.ellipse([CX - 40, POMMEL - 40, CX + 40, POMMEL + 40], fill=LIGHT + (255,))
bd.ellipse([CX - 74, POMMEL - 74, CX + 74, POMMEL + 74], outline=GOLD_BRIGHT + (255,), width=8)

# cyan glow beneath the blade, then the crisp blade
glow(img, lambda g: g.polygon(blade_pts, fill=LIGHT + (170,)), blur=90)
glow(img, lambda g: g.polygon([
    (CX, TIP + 120), (CX + BW * 0.30, TIP + 260), (CX + BW * 0.30, GUARD - 30),
    (CX - BW * 0.30, GUARD - 30), (CX - BW * 0.30, TIP + 260)], fill=WHITE + (255,)), blur=34)
img.alpha_composite(blade)

# tip flare + a few sparkles riding the blade
glow(img, lambda g: sparkle(g, CX, TIP + 8, 130, WHITE + (255,)), blur=16)
sp = Image.new("RGBA", img.size, (0, 0, 0, 0))
sd = ImageDraw.Draw(sp)
sparkle(sd, CX, TIP + 8, 110, WHITE + (255,))
sparkle(sd, CX - 150, CY - 260, 44, LIGHT + (220,))
sparkle(sd, CX + 170, CY - 80, 56, LIGHT + (200,))
sparkle(sd, CX + 120, CY - 470, 36, WHITE + (190,))
img.alpha_composite(sp)

# ----- vignette ----------------------------------------------------------------------
vig = Image.new("L", img.size, 0)
vd = ImageDraw.Draw(vig)
vd.ellipse([-S * 0.18, -S * 0.18, S * 1.18, S * 1.18], fill=255)
vig = vig.filter(ImageFilter.GaussianBlur(180))
black = Image.new("RGBA", img.size, (4, 5, 12, 255))
img = Image.composite(img, black, vig)

# ----- downscale + save --------------------------------------------------------------
final = img.convert("RGB").resize((512, 512), Image.LANCZOS)
import os
out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "icon.png")
final.save(out, "PNG", optimize=True)
print("written:", os.path.abspath(out), os.path.getsize(out), "bytes")
