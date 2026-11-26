#!/usr/bin/env python3
"""svg2lvgl.py - offline SVG rasterizer -> LVGL 9.2 C image arrays.

The touch UI is built with LV_USE_VECTOR_GRAPHIC=0, so every icon is
pre-rendered at build time by this tool and compiled in as an lv_image_dsc_t.

Usage
-----
    python3 svg2lvgl.py [--manifest icons_manifest.py] [--out-dir ../ui/kit]
                        [--preview DIR]

All default paths are relative to this script's own directory. The tool writes
<out-dir>/icons_gen.hpp and <out-dir>/icons_gen.cpp. With --preview it also
writes one PNG per icon (a8 = black on white, argb = composited on #eef1f5,
scaled 4x nearest) plus a contact sheet _sheet.png.

Manifest: see icons_manifest.py (ICONS = [dict(name=, svg=, w=, h=, mode=,
color=), ...]).

Dependencies: python3 stdlib + numpy (PIL only for --preview PNGs).

Renderer
--------
* Fill: exact non-zero / even-odd winding on an 8x8 supersample grid per pixel
  (64 coverage levels), fully vectorised with numpy (edge crossings -> cumsum).
* Curves/arcs are flattened adaptively (Wang's formula, ~0.05 px tolerance).
* Strokes are built as a union of polygons (segment quads + join wedges /
  discs + caps), rasterised with the same non-zero filler, so overlapping
  stroke parts never double-darken.
* Paint order: fill then stroke per element, source-over in document order.
  Group opacity is approximated by multiplying it into each element.
* Gradients (linear/radial, objectBoundingBox/userSpaceOnUse,
  gradientTransform, href inheritance, focal point) are evaluated at pixel
  centres; spreadMethod is always "pad".

Not supported (silently ignored): <text>/<tspan>, <style> sheets / class
selectors, clipPath, mask, pattern, filter, marker, <image>, clipping of nested
<svg> viewports, spreadMethod reflect/repeat, vector-effect, paint-order,
percent stroke widths.
"""

import argparse
import math
import os
import re
import runpy
import sys
import time
import xml.etree.ElementTree as ET

import numpy as np

SS = 8                 # supersamples per pixel per axis
FLAT_TOL = 0.05        # curve flattening tolerance in device pixels
DISC_TOL = 0.05        # round-join/cap polygon tolerance in device pixels

# --------------------------------------------------------------------------
# small parsing helpers
# --------------------------------------------------------------------------

NUM_RE = re.compile(r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')

NAMED_COLORS = {
    'black': (0, 0, 0), 'white': (255, 255, 255), 'red': (255, 0, 0),
    'green': (0, 128, 0), 'lime': (0, 255, 0), 'blue': (0, 0, 255),
    'yellow': (255, 255, 0), 'cyan': (0, 255, 255), 'aqua': (0, 255, 255),
    'magenta': (255, 0, 255), 'fuchsia': (255, 0, 255),
    'gray': (128, 128, 128), 'grey': (128, 128, 128),
    'silver': (192, 192, 192), 'maroon': (128, 0, 0), 'olive': (128, 128, 0),
    'navy': (0, 0, 128), 'purple': (128, 0, 128), 'teal': (0, 128, 128),
    'orange': (255, 165, 0), 'darkgray': (169, 169, 169),
    'darkgrey': (169, 169, 169), 'lightgray': (211, 211, 211),
    'lightgrey': (211, 211, 211), 'dimgray': (105, 105, 105),
    'dimgrey': (105, 105, 105), 'gainsboro': (220, 220, 220),
    'whitesmoke': (245, 245, 245), 'darkred': (139, 0, 0),
    'darkgreen': (0, 100, 0), 'darkblue': (0, 0, 139),
    'steelblue': (70, 130, 180), 'skyblue': (135, 206, 235),
    'lightblue': (173, 216, 230), 'royalblue': (65, 105, 225),
    'dodgerblue': (30, 144, 255), 'gold': (255, 215, 0),
    'crimson': (220, 20, 60), 'tomato': (255, 99, 71),
    'orangered': (255, 69, 0), 'limegreen': (50, 205, 50),
    'seagreen': (46, 139, 87), 'slategray': (112, 128, 144),
    'slategrey': (112, 128, 144), 'lightslategray': (119, 136, 153),
    'darkslategray': (47, 79, 79), 'brown': (165, 42, 42),
    'pink': (255, 192, 203), 'violet': (238, 130, 238),
    'indigo': (75, 0, 130), 'beige': (245, 245, 220),
}


def parse_float(s, default=0.0):
    if s is None:
        return default
    m = NUM_RE.match(s.strip())
    return float(m.group(0)) if m else default


def parse_length(s, ref, default=0.0):
    """Length with optional unit; percentages are relative to ``ref``."""
    if s is None:
        return default
    s = s.strip()
    m = NUM_RE.match(s)
    if not m:
        return default
    v = float(m.group(0))
    unit = s[m.end():].strip()
    if unit == '%':
        return v * ref / 100.0
    return v  # px / unitless (other units treated as px)


def parse_numlist(s):
    return [float(x) for x in NUM_RE.findall(s or '')]


def parse_color(s, current):
    """Return (r, g, b, a) with components 0..1, or None for 'none'."""
    s = s.strip()
    low = s.lower()
    if low in ('none', 'transparent'):
        return None
    if low == 'currentcolor':
        return current
    if s.startswith('#'):
        h = s[1:]
        if len(h) in (3, 4):
            h = ''.join(c * 2 for c in h)
        if len(h) in (6, 8):
            try:
                v = [int(h[i:i + 2], 16) / 255.0 for i in range(0, len(h), 2)]
            except ValueError:
                v = None
            if v is not None:
                return (v[0], v[1], v[2], v[3] if len(v) == 4 else 1.0)
    m = re.match(r'rgba?\(([^)]*)\)', low)
    if m:
        parts = [p.strip() for p in re.split(r'[,\s/]+', m.group(1)) if p.strip()]
        ch = []
        for i, p in enumerate(parts[:4]):
            if p.endswith('%'):
                f = float(p[:-1]) / 100.0
                ch.append(f if i < 3 else f)
            else:
                f = float(p)
                ch.append(f / 255.0 if i < 3 else f)
        while len(ch) < 3:
            ch.append(0.0)
        if len(ch) == 3:
            ch.append(1.0)
        return tuple(min(1.0, max(0.0, c)) for c in ch)
    if low in NAMED_COLORS:
        r, g, b = NAMED_COLORS[low]
        return (r / 255.0, g / 255.0, b / 255.0, 1.0)
    sys.stderr.write('svg2lvgl: warning: unknown color %r, using black\n' % s)
    return (0.0, 0.0, 0.0, 1.0)


def parse_style_attr(s):
    out = {}
    for decl in (s or '').split(';'):
        if ':' in decl:
            k, v = decl.split(':', 1)
            v = v.replace('!important', '').strip()
            out[k.strip().lower()] = v
    return out


# --------------------------------------------------------------------------
# affine matrices: (a, b, c, d, e, f) as numpy 2x3 [[a c e], [b d f]]
# --------------------------------------------------------------------------

def mat(a, b, c, d, e, f):
    return np.array([[a, c, e], [b, d, f]], dtype=np.float64)


IDENT = mat(1, 0, 0, 1, 0, 0)


def mmul(m1, m2):
    """m1 * m2 (apply m2 first)."""
    a = np.vstack([m1, [0, 0, 1]])
    b = np.vstack([m2, [0, 0, 1]])
    return (a @ b)[:2]


def minv(m):
    a = np.vstack([m, [0, 0, 1]])
    return np.linalg.inv(a)[:2]


def apply(m, pts):
    return pts @ m[:, :2].T + m[:, 2]


def mscale(m):
    """Largest singular value of the linear part (for tolerances)."""
    s = np.linalg.svd(m[:, :2], compute_uv=False)
    return float(max(s[0], 1e-9))


TF_RE = re.compile(r'(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)')


def parse_transform(s):
    m = IDENT.copy()
    if not s:
        return m
    for name, args in TF_RE.findall(s):
        v = parse_numlist(args)
        if name == 'matrix' and len(v) == 6:
            t = mat(*v)
        elif name == 'translate' and v:
            t = mat(1, 0, 0, 1, v[0], v[1] if len(v) > 1 else 0.0)
        elif name == 'scale' and v:
            t = mat(v[0], 0, 0, v[1] if len(v) > 1 else v[0], 0, 0)
        elif name == 'rotate' and v:
            a = math.radians(v[0])
            ca, sa = math.cos(a), math.sin(a)
            t = mat(ca, sa, -sa, ca, 0, 0)
            if len(v) >= 3:
                cx, cy = v[1], v[2]
                t = mmul(mat(1, 0, 0, 1, cx, cy), mmul(t, mat(1, 0, 0, 1, -cx, -cy)))
        elif name == 'skewX' and v:
            t = mat(1, 0, math.tan(math.radians(v[0])), 1, 0, 0)
        elif name == 'skewY' and v:
            t = mat(1, math.tan(math.radians(v[0])), 0, 1, 0, 0)
        else:
            continue
        m = mmul(m, t)
    return m


def viewbox_transform(vb, par, x, y, w, h):
    """Map viewBox (vx, vy, vw, vh) onto viewport x, y, w, h."""
    vx, vy, vw, vh = vb
    if vw <= 0 or vh <= 0:
        return mat(1, 0, 0, 1, x, y)
    sx, sy = w / vw, h / vh
    par = (par or 'xMidYMid meet').split()
    align = par[0] if par else 'xMidYMid'
    slice_ = len(par) > 1 and par[1] == 'slice'
    if align == 'none':
        return mat(sx, 0, 0, sy, x - vx * sx, y - vy * sy)
    s = max(sx, sy) if slice_ else min(sx, sy)
    tx = x - vx * s
    ty = y - vy * s
    ax = align[1:4].lower() if len(align) >= 8 else 'mid'
    ay = align[5:8].lower() if len(align) >= 8 else 'mid'
    if ax == 'mid':
        tx += (w - vw * s) / 2
    elif ax == 'max':
        tx += (w - vw * s)
    if ay == 'mid':
        ty += (h - vh * s) / 2
    elif ay == 'max':
        ty += (h - vh * s)
    return mat(s, 0, 0, s, tx, ty)


# --------------------------------------------------------------------------
# path data -> subpaths of segments (user space)
# --------------------------------------------------------------------------

class _Scan:
    def __init__(self, d):
        self.d = d
        self.i = 0
        self.n = len(d)

    def skip(self):
        while self.i < self.n and self.d[self.i] in ' \t\r\n,':
            self.i += 1

    def eof(self):
        self.skip()
        return self.i >= self.n

    def has_num(self):
        self.skip()
        return self.i < self.n and (self.d[self.i].isdigit() or self.d[self.i] in '+-.')

    def num(self):
        self.skip()
        m = NUM_RE.match(self.d, self.i)
        if not m:
            raise ValueError('number expected at %d' % self.i)
        self.i = m.end()
        return float(m.group(0))

    def flag(self):
        self.skip()
        if self.i < self.n and self.d[self.i] in '01':
            self.i += 1
            return self.d[self.i - 1] == '1'
        raise ValueError('flag expected at %d' % self.i)


def arc_to_cubics(x1, y1, rx, ry, phi_deg, fa, fs, x2, y2):
    if x1 == x2 and y1 == y2:
        return []
    rx, ry = abs(rx), abs(ry)
    if rx == 0 or ry == 0:
        return [('L', (x2, y2))]
    phi = math.radians(phi_deg % 360.0)
    cp, sp = math.cos(phi), math.sin(phi)
    dx, dy = (x1 - x2) / 2.0, (y1 - y2) / 2.0
    x1p = cp * dx + sp * dy
    y1p = -sp * dx + cp * dy
    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
    if lam > 1:
        s = math.sqrt(lam)
        rx *= s
        ry *= s
    num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    coef = math.sqrt(max(0.0, num / den)) if den > 0 else 0.0
    if fa == fs:
        coef = -coef
    cxp = coef * rx * y1p / ry
    cyp = -coef * ry * x1p / rx
    cx = cp * cxp - sp * cyp + (x1 + x2) / 2.0
    cy = sp * cxp + cp * cyp + (y1 + y2) / 2.0

    def ang(ux, uy, vx, vy):
        return math.atan2(ux * vy - uy * vx, ux * vx + uy * vy)

    ux, uy = (x1p - cxp) / rx, (y1p - cyp) / ry
    vx, vy = (-x1p - cxp) / rx, (-y1p - cyp) / ry
    th1 = ang(1.0, 0.0, ux, uy)
    dth = ang(ux, uy, vx, vy)
    if not fs and dth > 0:
        dth -= 2 * math.pi
    elif fs and dth < 0:
        dth += 2 * math.pi
    nseg = max(1, int(math.ceil(abs(dth) / (math.pi / 2) - 1e-9)))
    d = dth / nseg
    t = 4.0 / 3.0 * math.tan(d / 4.0)

    def mp(u, v):
        return (cx + rx * u * cp - ry * v * sp, cy + rx * u * sp + ry * v * cp)

    out = []
    for i in range(nseg):
        a0 = th1 + i * d
        a1 = a0 + d
        c0, s0, c1, s1 = math.cos(a0), math.sin(a0), math.cos(a1), math.sin(a1)
        p1 = mp(c0 - t * s0, s0 + t * c0)
        p2 = mp(c1 + t * s1, s1 - t * c1)
        p3 = (x2, y2) if i == nseg - 1 else mp(c1, s1)
        out.append(('C', p1, p2, p3))
    return out


class Subpath:
    __slots__ = ('start', 'segs', 'closed')

    def __init__(self, start):
        self.start = start
        self.segs = []      # ('L', p) | ('Q', c, p) | ('C', c1, c2, p)
        self.closed = False


def parse_path(d):
    sc = _Scan(d or '')
    subs = []
    cur = None
    x = y = 0.0
    sx = sy = 0.0
    lc = None          # last control point (for S/T)
    lcmd = ''
    cmd = None

    def need():
        nonlocal cur
        if cur is None:
            cur = Subpath((x, y))
            subs.append(cur)
        return cur

    try:
        while not sc.eof():
            ch = sc.d[sc.i]
            if ch.isalpha():
                sc.i += 1
                cmd = ch
                if cmd in 'Zz':
                    if cur is not None:
                        cur.closed = True
                        x, y = cur.start
                    cur = None
                    lcmd = 'Z'
                    continue
                if cmd not in 'MmLlHhVvCcSsQqTtAa':
                    break
            elif cmd is None or cmd in 'Zz':
                break
            elif cmd == 'M':
                cmd = 'L'
            elif cmd == 'm':
                cmd = 'l'
            rel = cmd.islower()
            C = cmd.upper()
            ox, oy = (x, y) if rel else (0.0, 0.0)
            if C == 'M':
                x, y = ox + sc.num(), oy + sc.num()
                cur = Subpath((x, y))
                subs.append(cur)
                sx, sy = x, y
                lc = None
            elif C == 'L':
                nx, ny = ox + sc.num(), oy + sc.num()
                need().segs.append(('L', (nx, ny)))
                x, y = nx, ny
                lc = None
            elif C == 'H':
                nx = (x if rel else 0.0) + sc.num()
                need().segs.append(('L', (nx, y)))
                x = nx
                lc = None
            elif C == 'V':
                ny = (y if rel else 0.0) + sc.num()
                need().segs.append(('L', (x, ny)))
                y = ny
                lc = None
            elif C == 'C':
                c1 = (ox + sc.num(), oy + sc.num())
                c2 = (ox + sc.num(), oy + sc.num())
                p = (ox + sc.num(), oy + sc.num())
                need().segs.append(('C', c1, c2, p))
                lc = c2
                x, y = p
            elif C == 'S':
                if lcmd in 'CS' and lc is not None:
                    c1 = (2 * x - lc[0], 2 * y - lc[1])
                else:
                    c1 = (x, y)
                c2 = (ox + sc.num(), oy + sc.num())
                p = (ox + sc.num(), oy + sc.num())
                need().segs.append(('C', c1, c2, p))
                lc = c2
                x, y = p
            elif C == 'Q':
                c = (ox + sc.num(), oy + sc.num())
                p = (ox + sc.num(), oy + sc.num())
                need().segs.append(('Q', c, p))
                lc = c
                x, y = p
            elif C == 'T':
                if lcmd in 'QT' and lc is not None:
                    c = (2 * x - lc[0], 2 * y - lc[1])
                else:
                    c = (x, y)
                p = (ox + sc.num(), oy + sc.num())
                need().segs.append(('Q', c, p))
                lc = c
                x, y = p
            elif C == 'A':
                rx, ry, rot = sc.num(), sc.num(), sc.num()
                fa, fs = sc.flag(), sc.flag()
                p = (ox + sc.num(), oy + sc.num())
                sp_ = need()
                segs = arc_to_cubics(x, y, rx, ry, rot, fa, fs, p[0], p[1])
                if not segs:
                    segs = [('L', p)]
                sp_.segs.extend(segs)
                x, y = p
                lc = None
            lcmd = C
    except (ValueError, IndexError):
        pass  # per spec: render up to the first error
    del sx, sy
    return subs


def flatten(subs, tol):
    """-> list of (points Nx2 array, closed, has_segments)."""
    out = []
    for s in subs:
        pts = [s.start]
        p0 = s.start
        for seg in s.segs:
            if seg[0] == 'L':
                pts.append(seg[1])
                p0 = seg[1]
                continue
            if seg[0] == 'Q':
                c, p = np.array(seg[1]), np.array(seg[2])
                a = np.array(p0)
                M = np.hypot(*(a - 2 * c + p))
                n = int(min(400, max(1, math.ceil(math.sqrt(0.25 * M / tol)))))
                t = np.linspace(0.0, 1.0, n + 1)[1:, None]
                mt = 1 - t
                q = mt * mt * a + 2 * mt * t * c + t * t * p
            else:
                a = np.array(p0)
                c1, c2, p = np.array(seg[1]), np.array(seg[2]), np.array(seg[3])
                M = max(np.hypot(*(a - 2 * c1 + c2)), np.hypot(*(c1 - 2 * c2 + p)))
                n = int(min(400, max(1, math.ceil(math.sqrt(0.75 * M / tol)))))
                t = np.linspace(0.0, 1.0, n + 1)[1:, None]
                mt = 1 - t
                q = (mt ** 3) * a + 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + (t ** 3) * p
            q[-1] = p
            pts.extend(map(tuple, q))
            p0 = tuple(p)
        out.append((np.array(pts, dtype=np.float64), s.closed, bool(s.segs)))
    return out


# --------------------------------------------------------------------------
# shapes -> path segments
# --------------------------------------------------------------------------

def ellipse_subpath(cx, cy, rx, ry):
    s = Subpath((cx + rx, cy))
    s.segs += arc_to_cubics(cx + rx, cy, rx, ry, 0, False, True, cx, cy + ry)
    s.segs += arc_to_cubics(cx, cy + ry, rx, ry, 0, False, True, cx - rx, cy)
    s.segs += arc_to_cubics(cx - rx, cy, rx, ry, 0, False, True, cx, cy - ry)
    s.segs += arc_to_cubics(cx, cy - ry, rx, ry, 0, False, True, cx + rx, cy)
    s.closed = True
    return [s]


def rect_subpaths(x, y, w, h, rx, ry):
    if w <= 0 or h <= 0:
        return []
    rx = min(rx, w / 2.0)
    ry = min(ry, h / 2.0)
    if rx <= 0 or ry <= 0:
        s = Subpath((x, y))
        s.segs = [('L', (x + w, y)), ('L', (x + w, y + h)), ('L', (x, y + h))]
        s.closed = True
        return [s]
    s = Subpath((x + rx, y))
    s.segs.append(('L', (x + w - rx, y)))
    s.segs += arc_to_cubics(x + w - rx, y, rx, ry, 0, False, True, x + w, y + ry)
    s.segs.append(('L', (x + w, y + h - ry)))
    s.segs += arc_to_cubics(x + w, y + h - ry, rx, ry, 0, False, True, x + w - rx, y + h)
    s.segs.append(('L', (x + rx, y + h)))
    s.segs += arc_to_cubics(x + rx, y + h, rx, ry, 0, False, True, x, y + h - ry)
    s.segs.append(('L', (x, y + ry)))
    s.segs += arc_to_cubics(x, y + ry, rx, ry, 0, False, True, x + rx, y)
    s.closed = True
    return [s]


def poly_subpaths(pts_attr, closed):
    v = parse_numlist(pts_attr)
    if len(v) < 2:
        return []
    pts = list(zip(v[0::2], v[1::2]))
    s = Subpath(pts[0])
    s.segs = [('L', p) for p in pts[1:]]
    s.closed = closed
    return [s]


# --------------------------------------------------------------------------
# stroking (user space) -> polygons
# --------------------------------------------------------------------------

def _dedupe(P, closed):
    if len(P) > 1:
        keep = np.ones(len(P), bool)
        keep[1:] = np.any(np.abs(np.diff(P, axis=0)) > 1e-9, axis=1)
        P = P[keep]
    if closed and len(P) > 1 and np.all(np.abs(P[0] - P[-1]) <= 1e-9):
        P = P[:-1]
    return P


def dash_polyline(P, closed, dashes, offset):
    """Split a polyline into dash polylines (all open)."""
    if closed:
        P = np.vstack([P, P[:1]])
    seg = np.diff(P, axis=0)
    L = np.hypot(seg[:, 0], seg[:, 1])
    total = float(L.sum())
    period = float(sum(dashes))
    if total <= 0 or period <= 0:
        return []
    cum = np.concatenate([[0.0], np.cumsum(L)])

    def point_at(s):
        i = int(np.searchsorted(cum, s, side='right') - 1)
        i = min(max(i, 0), len(L) - 1)
        if L[i] <= 0:
            return P[i]
        return P[i] + seg[i] * ((s - cum[i]) / L[i])

    out = []
    # position within the pattern at s=0
    off = offset % period
    idx = 0
    while off >= dashes[idx]:
        off -= dashes[idx]
        idx = (idx + 1) % len(dashes)
    s = -off
    guard = 0
    while s < total and guard < 100000:
        guard += 1
        e = s + dashes[idx]
        if idx % 2 == 0:
            a, b = max(s, 0.0), min(e, total)
            if b > a or (b == a and dashes[idx] == 0):
                inner = [P[k] for k in range(len(cum)) if a < cum[k] < b]
                pts = [point_at(a)] + inner + [point_at(b)]
                out.append(np.array(pts))
        s = e
        idx = (idx + 1) % len(dashes)
    return out


def _disc_poly(n):
    a = np.linspace(0.0, 2 * math.pi, n, endpoint=False)
    return np.stack([np.cos(a), np.sin(a)], axis=1)


def stroke_polygons(P, closed, has_segs, hw, cap, join, miterlimit, dev_scale):
    """Return list of (K, M, 2) polygon batches (user space) whose non-zero
    union is the stroke of polyline P with half width hw."""
    P = _dedupe(P, closed)
    r_dev = hw * dev_scale
    if r_dev > DISC_TOL:
        nd = int(math.ceil(math.pi / math.acos(max(-1.0, 1.0 - DISC_TOL / r_dev))))
    else:
        nd = 8
    nd = min(256, max(8, nd))
    unit = _disc_poly(nd) * hw
    batches = []
    if len(P) == 1:
        if not has_segs:
            return batches
        c = P[0]
        if cap == 'round':
            batches.append((c + unit)[None])
        elif cap == 'square':
            batches.append(np.array([[[c[0] - hw, c[1] - hw], [c[0] + hw, c[1] - hw],
                                      [c[0] + hw, c[1] + hw], [c[0] - hw, c[1] + hw]]]))
        return batches
    if len(P) == 0:
        return batches
    V = np.vstack([P, P[:1]]) if closed else P
    A, B = V[:-1].copy(), V[1:].copy()
    d = B - A
    L = np.hypot(d[:, 0], d[:, 1])
    u = d / L[:, None]
    n = np.stack([-u[:, 1], u[:, 0]], axis=1) * hw
    if not closed and cap == 'square':
        A[0] -= u[0] * hw
        B[-1] += u[-1] * hw
    quads = np.stack([A + n, B + n, B - n, A - n], axis=1)
    batches.append(quads)
    if not closed and cap == 'round':
        batches.append(np.stack([P[0] + unit, P[-1] + unit]))
    # joins
    if closed:
        jv = V[:-1]                       # vertex i joins seg i-1 and seg i
        uin, uout = np.roll(u, 1, axis=0), u
        nin, nout = np.roll(n, 1, axis=0), n
    else:
        jv = P[1:-1]
        uin, uout = u[:-1], u[1:]
        nin, nout = n[:-1], n[1:]
    if len(jv):
        cross = uin[:, 0] * uout[:, 1] - uin[:, 1] * uout[:, 0]
        dot = np.sum(uin * uout, axis=1)
        turn = np.arctan2(np.abs(cross), dot)
        sel = turn > 1e-9
        jv, nin, nout, cross, turn = jv[sel], nin[sel], nout[sel], cross[sel], turn[sel]
        if len(jv):
            s = np.where(cross > 0, -1.0, 1.0)[:, None]
            oin, oout = nin * s, nout * s
            bis = oin + oout
            denom = hw * hw + np.sum(oin * oout, axis=1)
            k = np.where(denom > 1e-12, hw * hw / np.maximum(denom, 1e-12), 0.0)
            tip = bis * k[:, None]
            ratio = np.hypot(tip[:, 0], tip[:, 1]) / hw
            if join == 'round':
                big = turn > 0.25
                small = ~big
                if np.any(big):
                    batches.append(jv[big][:, None, :] + unit[None])
                use_miter = small
            elif join == 'miter' or join == 'miter-clip' or join == 'arcs':
                use_miter = (ratio <= miterlimit) & (denom > 1e-12)
            else:
                use_miter = np.zeros(len(jv), bool)
            wedge_tip = np.where(use_miter[:, None], tip, (oin + oout) * 0.5)
            if join != 'round':
                wedges = np.stack([jv, jv + oin, jv + wedge_tip, jv + oout], axis=1)
                batches.append(wedges)
            elif np.any(use_miter):
                m = use_miter
                wedges = np.stack([jv[m], jv[m] + oin[m], jv[m] + tip[m], jv[m] + oout[m]], axis=1)
                batches.append(wedges)
    return batches


# --------------------------------------------------------------------------
# scan conversion
# --------------------------------------------------------------------------

def _orient(batch):
    """Make every polygon in a (K, M, 2) batch positively oriented."""
    x, y = batch[..., 0], batch[..., 1]
    area = np.sum(x * np.roll(y, -1, axis=1) - np.roll(x, -1, axis=1) * y, axis=1)
    neg = area < 0
    if np.any(neg):
        batch = batch.copy()
        batch[neg] = batch[neg][:, ::-1]
    return batch


def rasterize(polys, evenodd, W, H):
    """polys: list of (M,2) or (K,M,2) arrays in device pixel coords.
    Returns (coverage float array, (y0, x0)) or None."""
    e0, e1 = [], []
    for p in polys:
        if p.ndim == 2:
            if len(p) < 2:
                continue
            p = p[None]
        e0.append(p.reshape(-1, 2))
        e1.append(np.roll(p, -1, axis=1).reshape(-1, 2))
    if not e0:
        return None
    a = np.concatenate(e0) * SS
    b = np.concatenate(e1) * SS
    keep = a[:, 1] != b[:, 1]
    a, b = a[keep], b[keep]
    if len(a) == 0:
        return None
    allx = np.concatenate([a[:, 0], b[:, 0]])
    ally = np.concatenate([a[:, 1], b[:, 1]])
    X0 = int(max(0, math.floor(allx.min() / SS)))
    X1 = int(min(W, math.ceil(allx.max() / SS)))
    Y0 = int(max(0, math.floor(ally.min() / SS)))
    Y1 = int(min(H, math.ceil(ally.max() / SS)))
    if X1 <= X0 or Y1 <= Y0:
        return None
    Ws, Hs = (X1 - X0) * SS, (Y1 - Y0) * SS
    ox, oy = X0 * SS, Y0 * SS
    x0, y0 = a[:, 0] - ox, a[:, 1] - oy
    x1, y1 = b[:, 0] - ox, b[:, 1] - oy
    dirs = np.where(y1 > y0, 1.0, -1.0)
    ymin = np.minimum(y0, y1)
    ymax = np.maximum(y0, y1)
    jmin = np.clip(np.ceil(ymin - 0.5), 0, Hs).astype(np.int64)
    jmax = np.clip(np.ceil(ymax - 0.5), 0, Hs).astype(np.int64)
    cnt = np.maximum(jmax - jmin, 0)
    total = int(cnt.sum())
    if total == 0:
        return None
    ei = np.repeat(np.arange(len(cnt)), cnt)
    starts = np.cumsum(cnt) - cnt
    row = jmin[ei] + (np.arange(total) - np.repeat(starts, cnt))
    yc = row + 0.5
    xs = x0[ei] + (yc - y0[ei]) * (x1[ei] - x0[ei]) / (y1[ei] - y0[ei])
    col = np.clip(np.ceil(xs - 0.5), 0, Ws).astype(np.int64)
    flat = row * (Ws + 1) + col
    delta = np.bincount(flat, weights=dirs[ei], minlength=Hs * (Ws + 1))
    wind = np.cumsum(delta.reshape(Hs, Ws + 1), axis=1)[:, :Ws]
    wi = np.rint(wind).astype(np.int64)
    mask = (wi & 1) != 0 if evenodd else wi != 0
    cov = mask.reshape(Y1 - Y0, SS, X1 - X0, SS).mean(axis=(1, 3))
    return cov, (Y0, X0)


# --------------------------------------------------------------------------
# document rendering
# --------------------------------------------------------------------------

INHERITED = {
    'fill': 'black', 'stroke': 'none', 'stroke-width': '1',
    'stroke-linecap': 'butt', 'stroke-linejoin': 'miter',
    'stroke-miterlimit': '4', 'stroke-dasharray': 'none',
    'stroke-dashoffset': '0', 'fill-opacity': '1', 'stroke-opacity': '1',
    'fill-rule': 'nonzero', 'color': None, 'visibility': 'visible',
}
NONINHERITED = ('opacity', 'display', 'stop-color', 'stop-opacity')

SKIP_TAGS = {
    'defs', 'linearGradient', 'radialGradient', 'title', 'desc', 'metadata',
    'text', 'tspan', 'textPath', 'style', 'script', 'animate',
    'animateTransform', 'animateMotion', 'animateColor', 'set', 'clipPath',
    'mask', 'pattern', 'symbol', 'marker', 'filter', 'image', 'foreignObject',
    'stop',
}


def local(tag):
    return tag.rsplit('}', 1)[-1] if isinstance(tag, str) else ''


def get_href(el):
    for k, v in el.attrib.items():
        if local(k) == 'href':
            return v
    return None


def element_props(el):
    props = {}
    for k, v in el.attrib.items():
        k = local(k)
        if k in INHERITED or k in NONINHERITED:
            props[k] = v.strip()
    props.update({k: v for k, v in parse_style_attr(el.get('style')).items()
                  if k in INHERITED or k in NONINHERITED})
    return props


class Renderer:
    def __init__(self, svg_text, W, H, color, mode):
        self.W, self.H = W, H
        self.mode = mode
        self.base_color = parse_color(color or '#000000', (0, 0, 0, 1))
        text = svg_text.strip()
        if 'xmlns:xlink' not in text and 'xlink:' in text:
            text = text.replace('<svg', '<svg xmlns:xlink="http://www.w3.org/1999/xlink"', 1)
        self.root = ET.fromstring(text)
        self.ids = {}
        for e in self.root.iter():
            i = e.get('id')
            if i is not None and i not in self.ids:
                self.ids[i] = e
        self.canvas = np.zeros((H, W, 4), dtype=np.float64)   # premultiplied
        self.use_depth = 0

    # ------------------------------------------------------------ helpers
    def render(self):
        r = self.root
        vb = parse_numlist(r.get('viewBox'))
        if len(vb) != 4:
            rw = parse_length(r.get('width'), self.W, 0.0)
            rh = parse_length(r.get('height'), self.H, 0.0)
            vb = [0.0, 0.0, rw or float(self.W), rh or float(self.H)]
        m = viewbox_transform(vb, r.get('preserveAspectRatio'), 0, 0, self.W, self.H)
        base = dict(INHERITED)
        base['color'] = None
        style, props = self._computed(r, base)   # root's own presentation attrs
        if props.get('display', '').strip() == 'none':
            return self.canvas
        op = min(1.0, max(0.0, parse_float(props.get('opacity'), 1.0)))
        if op <= 0:
            return self.canvas
        self._render_children(r, m, style, op, (vb[2], vb[3]))
        return self.canvas

    def _computed(self, el, parent_style):
        props = element_props(el)
        st = dict(parent_style)
        for k in INHERITED:
            if k in props and props[k] != 'inherit':
                st[k] = props[k]
        return st, props

    def _render_children(self, el, m, style, opacity, vp):
        for ch in el:
            self._render_el(ch, m, style, opacity, vp)

    def _render_el(self, el, m, pstyle, popacity, vp):
        tag = local(el.tag)
        if tag in SKIP_TAGS or not tag:
            return
        st, props = self._computed(el, pstyle)
        if props.get('display', '').strip() == 'none':
            return
        op = popacity * min(1.0, max(0.0, parse_float(props.get('opacity'), 1.0)))
        if op <= 0:
            return
        tm = mmul(m, parse_transform(el.get('transform')))
        vw, vh = vp
        if tag in ('g', 'a', 'switch'):
            self._render_children(el, tm, st, op, vp)
            return
        if tag == 'svg':
            x = parse_length(el.get('x'), vw)
            y = parse_length(el.get('y'), vh)
            w = parse_length(el.get('width'), vw, vw)
            h = parse_length(el.get('height'), vh, vh)
            if w <= 0 or h <= 0:
                return
            vb = parse_numlist(el.get('viewBox'))
            if len(vb) == 4:
                sm = viewbox_transform(vb, el.get('preserveAspectRatio'), x, y, w, h)
                nvp = (vb[2], vb[3])
            else:
                sm = mat(1, 0, 0, 1, x, y)
                nvp = (w, h)
            self._render_children(el, mmul(tm, sm), st, op, nvp)
            return
        if tag == 'use':
            href = get_href(el)
            ref = self.ids.get(href[1:]) if href and href.startswith('#') else None
            if ref is None or self.use_depth > 8:
                return
            x = parse_length(el.get('x'), vw)
            y = parse_length(el.get('y'), vh)
            um = mmul(tm, mat(1, 0, 0, 1, x, y))
            self.use_depth += 1
            if local(ref.tag) == 'symbol':
                st2, _ = self._computed(ref, st)
                vb = parse_numlist(ref.get('viewBox'))
                if len(vb) == 4:
                    w = parse_length(el.get('width'), vw, vb[2])
                    h = parse_length(el.get('height'), vh, vb[3])
                    um = mmul(um, viewbox_transform(vb, ref.get('preserveAspectRatio'), 0, 0, w, h))
                self._render_children(ref, um, st2, op, vp)
            else:
                self._render_el(ref, um, st, op, vp)
            self.use_depth -= 1
            return
        subs = self._shape(el, tag, vp)
        if subs:
            self._paint_shape(subs, st, tm, op, vp)

    def _shape(self, el, tag, vp):
        vw, vh = vp
        diag = math.hypot(vw, vh) / math.sqrt(2)
        L = parse_length
        if tag == 'path':
            return parse_path(el.get('d'))
        if tag == 'rect':
            rxs, rys = el.get('rx'), el.get('ry')
            rx = L(rxs, vw) if rxs not in (None, 'auto') else None
            ry = L(rys, vh) if rys not in (None, 'auto') else None
            if rx is None:
                rx = ry if ry is not None else 0.0
            if ry is None:
                ry = rx
            return rect_subpaths(L(el.get('x'), vw), L(el.get('y'), vh),
                                 L(el.get('width'), vw), L(el.get('height'), vh), rx, ry)
        if tag == 'circle':
            r = L(el.get('r'), diag)
            if r <= 0:
                return []
            return ellipse_subpath(L(el.get('cx'), vw), L(el.get('cy'), vh), r, r)
        if tag == 'ellipse':
            rx, ry = L(el.get('rx'), vw), L(el.get('ry'), vh)
            if rx <= 0 or ry <= 0:
                return []
            return ellipse_subpath(L(el.get('cx'), vw), L(el.get('cy'), vh), rx, ry)
        if tag == 'line':
            s = Subpath((L(el.get('x1'), vw), L(el.get('y1'), vh)))
            s.segs = [('L', (L(el.get('x2'), vw), L(el.get('y2'), vh)))]
            return [s]
        if tag == 'polyline':
            return poly_subpaths(el.get('points'), False)
        if tag == 'polygon':
            return poly_subpaths(el.get('points'), True)
        return []

    # ------------------------------------------------------------ paint
    def _current(self, st):
        c = st.get('color')
        if c is None:
            return self.base_color
        col = parse_color(c, self.base_color)
        return col if col is not None else self.base_color

    def _paint_spec(self, value, st):
        """-> None | ('solid', rgba) | ('grad', element)"""
        v = (value or 'none').strip()
        if v.startswith('url('):
            m = re.match(r'url\(\s*[\'"]?#([^\'")\s]+)[\'"]?\s*\)\s*(.*)', v)
            if m:
                ref = self.ids.get(m.group(1))
                if ref is not None and local(ref.tag) in ('linearGradient', 'radialGradient'):
                    return ('grad', ref)
                fb = m.group(2).strip()
                if fb:
                    return self._paint_spec(fb, st)
            return None
        c = parse_color(v, self._current(st))
        return None if c is None else ('solid', c)

    def _paint_shape(self, subs, st, tm, op, vp):
        if st.get('visibility') in ('hidden', 'collapse'):
            return
        scale = mscale(tm)
        flat = flatten(subs, FLAT_TOL / scale)
        bbox = None
        allp = [p for p, _, hs in flat if len(p)]
        if allp:
            cat = np.concatenate(allp)
            mn, mx = cat.min(axis=0), cat.max(axis=0)
            bbox = (mn[0], mn[1], mx[0] - mn[0], mx[1] - mn[1])
        # ---- fill
        fill = self._paint_spec(st['fill'], st)
        if fill is not None:
            polys = [apply(tm, p) for p, _, _ in flat if len(p) >= 3]
            if polys:
                res = rasterize(polys, st['fill-rule'].strip() == 'evenodd', self.W, self.H)
                if res is not None:
                    a = op * min(1.0, max(0.0, parse_float(st['fill-opacity'], 1.0)))
                    self._composite(res, fill, a, tm, bbox, vp, st)
        # ---- stroke
        stroke = self._paint_spec(st['stroke'], st)
        sw = parse_length(st['stroke-width'], math.hypot(*vp) / math.sqrt(2), 1.0)
        if stroke is not None and sw > 0:
            cap = st['stroke-linecap'].strip()
            join = st['stroke-linejoin'].strip()
            ml = max(1.0, parse_float(st['stroke-miterlimit'], 4.0))
            dashes = [] if st['stroke-dasharray'].strip() == 'none' else \
                [abs(x) for x in parse_numlist(st['stroke-dasharray'])]
            if len(dashes) % 2 == 1:
                dashes = dashes * 2
            if dashes and sum(dashes) <= 0:
                dashes = []
            doff = parse_float(st['stroke-dashoffset'], 0.0)
            batches = []
            for p, closed, hs in flat:
                if len(p) == 0:
                    continue
                if dashes:
                    pd = _dedupe(p, closed)
                    if len(pd) < 2:
                        pieces = [(pd, False, hs)]
                    else:
                        pieces = [(q, False, True) for q in dash_polyline(pd, closed, dashes, doff)]
                else:
                    pieces = [(p, closed, hs)]
                for q, c, h in pieces:
                    batches += stroke_polygons(q, c, h, sw / 2.0, cap, join, ml, scale)
            polys = [_orient(apply(tm, b.reshape(-1, 2)).reshape(b.shape)) for b in batches if len(b)]
            if polys:
                res = rasterize(polys, False, self.W, self.H)
                if res is not None:
                    a = op * min(1.0, max(0.0, parse_float(st['stroke-opacity'], 1.0)))
                    self._composite(res, stroke, a, tm, bbox, vp, st)

    def _gradient(self, gel, st, tm, bbox, vp, ys, xs):
        """Evaluate gradient at device pixel centres -> (h, w, 4) straight rgba."""
        chain = []
        e = gel
        while e is not None and e not in chain and len(chain) < 16:
            chain.append(e)
            h = get_href(e)
            e = self.ids.get(h[1:]) if h and h.startswith('#') else None

        def attr(name, default=None):
            for c in chain:
                if c.get(name) is not None:
                    return c.get(name)
            return default

        stops_el = next((c for c in chain if any(local(s.tag) == 'stop' for s in c)), None)
        offs, cols = [], []
        if stops_el is not None:
            last = 0.0
            for s in stops_el:
                if local(s.tag) != 'stop':
                    continue
                props = element_props(s)
                o = s.get('offset', '0').strip()
                ov = parse_float(o) / 100.0 if o.endswith('%') else parse_float(o)
                ov = min(1.0, max(last, ov))
                last = ov
                sc = props.get('stop-color', 'black')
                sst = dict(st)
                if 'color' in props:
                    sst['color'] = props['color']
                c = parse_color(sc, self._current(sst)) or (0, 0, 0, 0)
                so = min(1.0, max(0.0, parse_float(props.get('stop-opacity'), 1.0)))
                offs.append(ov)
                cols.append((c[0], c[1], c[2], c[3] * so))
        if not offs:
            return None
        units = attr('gradientUnits', 'objectBoundingBox')
        obb = units != 'userSpaceOnUse'
        if obb:
            if bbox is None or bbox[2] <= 0 or bbox[3] <= 0:
                return None
            bm = mat(bbox[2], 0, 0, bbox[3], bbox[0], bbox[1])
            rw = rh = rd = 1.0
        else:
            bm = IDENT
            rw, rh = vp
            rd = math.hypot(rw, rh) / math.sqrt(2)
        if obb:
            def L(s, ref, d):
                if s is None:
                    return d
                s = s.strip()
                return parse_float(s) / 100.0 if s.endswith('%') else parse_float(s)
        else:
            def L(s, ref, d):
                return parse_length(s, ref, d)
        full = mmul(mmul(tm, bm), parse_transform(attr('gradientTransform')))
        try:
            inv = minv(full)
        except np.linalg.LinAlgError:
            return None
        X, Y = np.meshgrid(xs + 0.5, ys + 0.5)
        gx = inv[0, 0] * X + inv[0, 1] * Y + inv[0, 2]
        gy = inv[1, 0] * X + inv[1, 1] * Y + inv[1, 2]
        if local(gel.tag) == 'linearGradient':
            x1 = L(attr('x1'), rw, 0.0)
            y1 = L(attr('y1'), rh, 0.0)
            x2 = L(attr('x2'), rw, 1.0 if obb else rw)
            y2 = L(attr('y2'), rh, 0.0)
            dx, dy = x2 - x1, y2 - y1
            dd = dx * dx + dy * dy
            if dd <= 0:
                t = np.ones_like(gx)
            else:
                t = ((gx - x1) * dx + (gy - y1) * dy) / dd
        else:
            cx = L(attr('cx'), rw, 0.5 if obb else rw / 2)
            cy = L(attr('cy'), rh, 0.5 if obb else rh / 2)
            r = L(attr('r'), rd, 0.5 if obb else rd / 2)
            fx = L(attr('fx'), rw, cx)
            fy = L(attr('fy'), rh, cy)
            if r <= 0:
                t = np.ones_like(gx)
            else:
                ex, ey = cx - fx, cy - fy
                el_ = math.hypot(ex, ey)
                if el_ > 0.99 * r:
                    k = 0.99 * r / el_
                    ex, ey = ex * k, ey * k
                    fx, fy = cx - ex, cy - ey
                qx, qy = gx - fx, gy - fy
                if ex == 0 and ey == 0:
                    t = np.hypot(qx, qy) / r
                else:
                    a = ex * ex + ey * ey - r * r
                    b = qx * ex + qy * ey
                    c = qx * qx + qy * qy
                    t = (b - np.sqrt(np.maximum(b * b - a * c, 0.0))) / a
        t = np.clip(t, 0.0, 1.0)
        cols = np.array(cols)
        out = np.empty(t.shape + (4,))
        for i in range(4):
            out[..., i] = np.interp(t, offs, cols[:, i])
        return out

    def _composite(self, res, paint, alpha, tm, bbox, vp, st):
        cov, (y0, x0) = res
        h, w = cov.shape
        if paint[0] == 'solid':
            c = paint[1]
            src_a = cov * (alpha * c[3])
            rgb = np.array(c[:3])[None, None, :]
        else:
            g = self._gradient(paint[1], st, tm, bbox, vp,
                               np.arange(y0, y0 + h, dtype=np.float64),
                               np.arange(x0, x0 + w, dtype=np.float64))
            if g is None:
                return
            src_a = cov * alpha * g[..., 3]
            rgb = g[..., :3]
        dst = self.canvas[y0:y0 + h, x0:x0 + w]
        inv = 1.0 - src_a
        dst[..., :3] = rgb * src_a[..., None] + dst[..., :3] * inv[..., None]
        dst[..., 3] = src_a + dst[..., 3] * inv


def render_icon(icon):
    W, H = int(icon['w']), int(icon['h'])
    r = Renderer(icon['svg'], W, H, icon.get('color', '#000000'), icon.get('mode', 'a8'))
    canvas = r.render()
    a = np.clip(canvas[..., 3], 0.0, 1.0)
    a8 = np.rint(a * 255.0).astype(np.uint8)
    if icon.get('mode', 'a8') == 'a8':
        return a8, None
    rgb = np.zeros((H, W, 3))
    nz = a > 1e-9
    rgb[nz] = canvas[nz, :3] / a[nz, None]
    rgb8 = np.rint(np.clip(rgb, 0.0, 1.0) * 255.0).astype(np.uint8)
    rgb8[a8 == 0] = 0
    bgra = np.dstack([rgb8[..., 2], rgb8[..., 1], rgb8[..., 0], a8])
    return a8, bgra


# --------------------------------------------------------------------------
# output
# --------------------------------------------------------------------------

IDENT_RE = re.compile(r'^[A-Za-z_][A-Za-z0-9_]*$')
CPP_KEYWORDS = {'find', 'make', 'delete', 'new', 'class', 'int', 'char', 'default',
                'switch', 'case', 'register', 'auto', 'template', 'operator'}


def hex_lines(data, per_line=16):
    out = []
    for i in range(0, len(data), per_line):
        chunk = data[i:i + per_line]
        out.append('    ' + ', '.join('0x%02x' % b for b in chunk) + ',')
    return '\n'.join(out)


def write_outputs(icons, results, out_dir, manifest_path):
    os.makedirs(out_dir, exist_ok=True)
    src = os.path.basename(manifest_path)
    hdr = [
        '// GENERATED FILE - DO NOT EDIT.',
        '// Generated by apps/reactor-hmi/assets/svg2lvgl.py from assets/%s.' % src,
        '// Re-run: python3 apps/reactor-hmi/assets/svg2lvgl.py',
        '#pragma once',
        '',
        '#include "lvgl.h"',
        '',
        'namespace reactor::hmi::icons {',
        '',
    ]
    for ic in icons:
        hdr.append('extern const lv_image_dsc_t %s;  // %dx%d %s' % (
            ic['name'], ic['w'], ic['h'], 'A8' if ic.get('mode', 'a8') == 'a8' else 'ARGB8888'))
    hdr += [
        '',
        '// Look up an icon by name (debug helper); returns nullptr if unknown.',
        'const lv_image_dsc_t* find(const char* name);',
        '',
        '}  // namespace reactor::hmi::icons',
        '',
    ]
    cpp = [
        '// GENERATED FILE - DO NOT EDIT.',
        '// Generated by apps/reactor-hmi/assets/svg2lvgl.py from assets/%s.' % src,
        '#include "icons_gen.hpp"',
        '',
        '#include <cstring>',
        '',
        'namespace reactor::hmi::icons {',
        '',
        'namespace {',
        '',
        'constexpr lv_image_dsc_t make(uint32_t cf, uint32_t w, uint32_t h, uint32_t stride,',
        '                              const uint8_t* d, uint32_t size) {',
        '    lv_image_dsc_t dsc{};',
        '    dsc.header.magic = LV_IMAGE_HEADER_MAGIC;',
        '    dsc.header.cf = cf;',
        '    dsc.header.flags = 0;',
        '    dsc.header.w = w;',
        '    dsc.header.h = h;',
        '    dsc.header.stride = stride;',
        '    dsc.header.reserved_2 = 0;',
        '    dsc.data_size = size;',
        '    dsc.data = d;',
        '    dsc.reserved = nullptr;',
        '    return dsc;',
        '}',
        '',
        '}  // namespace',
        '',
    ]
    for ic, (a8, bgra) in zip(icons, results):
        name, w, h = ic['name'], ic['w'], ic['h']
        if bgra is None:
            data = a8.tobytes()
            cf, stride = 'LV_COLOR_FORMAT_A8', w
        else:
            data = bgra.tobytes()
            cf, stride = 'LV_COLOR_FORMAT_ARGB8888', w * 4
        cpp.append('// %s: %dx%d %s' % (name, w, h, cf))
        cpp.append('static const uint8_t %s_data[%d] = {' % (name, len(data)))
        cpp.append(hex_lines(data))
        cpp.append('};')
        cpp.append('const lv_image_dsc_t %s = make(%s, %d, %d, %d, %s_data, %d);' % (
            name, cf, w, h, stride, name, len(data)))
        cpp.append('')
    cpp += [
        'namespace {',
        'struct Entry {',
        '    const char* name;',
        '    const lv_image_dsc_t* dsc;',
        '};',
        'const Entry kTable[] = {',
    ]
    for ic in icons:
        cpp.append('    {"%s", &%s},' % (ic['name'], ic['name']))
    cpp += [
        '};',
        '}  // namespace',
        '',
        'const lv_image_dsc_t* find(const char* name) {',
        '    if (name == nullptr) return nullptr;',
        '    for (const Entry& e : kTable) {',
        '        if (std::strcmp(e.name, name) == 0) return e.dsc;',
        '    }',
        '    return nullptr;',
        '}',
        '',
        '}  // namespace reactor::hmi::icons',
        '',
    ]
    hp = os.path.join(out_dir, 'icons_gen.hpp')
    cp = os.path.join(out_dir, 'icons_gen.cpp')
    for path, lines in ((hp, hdr), (cp, cpp)):
        text = '\n'.join(lines)
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
    return hp, cp


def write_previews(icons, results, pdir):
    from PIL import Image, ImageDraw  # preview only
    os.makedirs(pdir, exist_ok=True)
    bg = np.array([0xee, 0xf1, 0xf5], dtype=np.float64)
    tiles = []
    for ic, (a8, bgra) in zip(icons, results):
        if bgra is None:
            img = np.repeat((255 - a8)[..., None], 3, axis=2)
        else:
            a = bgra[..., 3:4] / 255.0
            rgb = bgra[..., [2, 1, 0]].astype(np.float64)
            img = np.rint(rgb * a + bg * (1 - a)).astype(np.uint8)
        img = np.repeat(np.repeat(img, 4, axis=0), 4, axis=1)
        im = Image.fromarray(img)
        im.save(os.path.join(pdir, ic['name'] + '.png'))
        tiles.append((ic['name'], im))
    if not tiles:
        return
    cols = min(6, len(tiles))
    cw = max(t[1].width for t in tiles) + 16
    ch = max(t[1].height for t in tiles) + 30
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new('RGB', (cols * cw, rows * ch), (200, 205, 212))
    dr = ImageDraw.Draw(sheet)
    for i, (name, im) in enumerate(tiles):
        x, y = (i % cols) * cw + 8, (i // cols) * ch + 8
        sheet.paste(im, (x, y))
        dr.text((x, y + im.height + 4), name, fill=(20, 20, 20))
    sheet.save(os.path.join(pdir, '_sheet.png'))


def load_manifest(path):
    ns = runpy.run_path(path)
    icons = ns.get('ICONS')
    if not isinstance(icons, (list, tuple)):
        raise SystemExit('svg2lvgl: %s does not define ICONS = [...]' % path)
    seen = set()
    out = []
    for i, ic in enumerate(icons):
        ic = dict(ic)
        for k in ('name', 'svg', 'w', 'h'):
            if k not in ic:
                raise SystemExit('svg2lvgl: icon #%d is missing %r' % (i, k))
        name = ic['name']
        if not IDENT_RE.match(name) or name in CPP_KEYWORDS or name.endswith('_data'):
            raise SystemExit('svg2lvgl: invalid icon name %r' % name)
        if name in seen:
            raise SystemExit('svg2lvgl: duplicate icon name %r' % name)
        seen.add(name)
        ic.setdefault('mode', 'a8')
        if ic['mode'] not in ('a8', 'argb'):
            raise SystemExit('svg2lvgl: icon %r: mode must be a8 or argb' % name)
        ic['w'], ic['h'] = int(ic['w']), int(ic['h'])
        if not (0 < ic['w'] <= 2048 and 0 < ic['h'] <= 2048):
            raise SystemExit('svg2lvgl: icon %r: bad size' % name)
        out.append(ic)
    return out


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description='Rasterize SVG icons into LVGL 9.2 image arrays.')
    ap.add_argument('--manifest', default='icons_manifest.py',
                    help='manifest file (default: icons_manifest.py next to this script)')
    ap.add_argument('--out-dir', default=os.path.join('..', 'ui', 'kit'),
                    help='output dir for icons_gen.hpp/.cpp (default: ../ui/kit)')
    ap.add_argument('--preview', metavar='DIR', help='also write PNG previews to DIR')
    args = ap.parse_args(argv)

    def rel(p):
        return p if os.path.isabs(p) else os.path.normpath(os.path.join(here, p))

    manifest = rel(args.manifest)
    out_dir = rel(args.out_dir)
    t0 = time.perf_counter()
    icons = load_manifest(manifest)
    results = []
    for ic in icons:
        try:
            results.append(render_icon(ic))
        except ET.ParseError as e:
            raise SystemExit('svg2lvgl: icon %r: SVG parse error: %s' % (ic['name'], e))
    hp, cp = write_outputs(icons, results, out_dir, manifest)
    if args.preview:
        write_previews(icons, results, os.path.abspath(args.preview))
    dt = time.perf_counter() - t0
    print('svg2lvgl: %d icons -> %s, %s (%.2f s)' % (len(icons), hp, cp, dt))


if __name__ == '__main__':
    main()
