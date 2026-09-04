# -*- coding: utf-8 -*-
"""参照画像から間取りを取り出す。

やり方は単純で、画像の全ての列について壁と床の取り合い (下向きの輝度段差) を
副画素で追い、歪みを外して床平面に戻す。得られた点列は「カメラから見えている
壁の足元の輪郭」そのものになる。壁は 2 方向しかないので、点列を軸に沿った
線分に分解すれば平面図になる。

出力は Level0Replica.cs にそのまま貼れる形の塊の一覧。奥行きは
「見えていない面が画面に出ない」ように、隣の面か画角の外まで伸ばして決める。

  python tools/extract-plan.py
"""
import json
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

REF = 'artifacts/reference/Backrooms_1.jpg'
CAL = json.load(open('artifacts/replica/calib.json'))
F, K1 = CAL['f'], CAL['k1']
PITCH_UP = np.radians(CAL['pitch'])
YAW = np.radians(36.59)
EYE = 1.345
CAM = np.array([4.533, EYE, 2.400])

im = Image.open(REF).convert('RGB')
W, H = im.size
CX, CY = W/2.0, H/2.0
A = np.asarray(im, np.float64)/255.
Y = A[..., 0]*.2126 + A[..., 1]*.7152 + A[..., 2]*.0722
Ys = ndimage.gaussian_filter(Y, 1.8)
gy = np.gradient(Ys, axis=0)

fwd = np.array([np.sin(YAW)*np.cos(PITCH_UP), np.sin(PITCH_UP), np.cos(YAW)*np.cos(PITCH_UP)])
right = np.array([np.cos(YAW), 0.0, -np.sin(YAW)])
up = np.cross(fwd, right)


def to_floor(u, v):
    """画像座標 -> 床平面 (world x,z)。歪みを外してから。"""
    xn = (u-CX)/F; yn = (v-CY)/F
    for _ in range(8):
        r2 = xn*xn + yn*yn
        d = 1.0 + K1*r2
        xn = (u-CX)/F/d
        yn = (v-CY)/F/d
    dirv = fwd + xn*right - yn*up
    t = (0.0 - CAM[1])/dirv[1]
    if t <= 0:
        return None
    p = CAM + t*dirv
    return p[0], p[2]


# ---- 全列で取り合いを追う ----
rows = []
for x in range(2, W-2):
    col = -gy[int(CY)+5:H-3, max(0, x-1):x+2].mean(1)
    k = int(np.argmax(col))
    g = col[k]
    if g < 0.010:
        rows.append((x, np.nan, 0.0)); continue
    a, b, c = col[max(k-1, 0)], col[k], col[min(k+1, len(col)-1)]
    d = 0.5*(a-c)/(a-2*b+c) if (a-2*b+c) != 0 else 0
    rows.append((x, int(CY)+5+k+d, g))
rows = np.array(rows)
good = ~np.isnan(rows[:, 1]) & (rows[:, 2] > 0.012)
print('取り合いを検出した列 %d / %d' % (good.sum(), len(rows)))

pts, cols = [], []
for x, v, g in rows[good]:
    p = to_floor(x, v)
    if p is None or not (0 < p[0] < 60) or not (0 < p[1] < 60):
        continue
    pts.append(p); cols.append(x)
pts = np.array(pts); cols = np.array(cols)
print('床平面に戻せた点 %d 個   x %.1f..%.1f   z %.1f..%.1f'
      % (len(pts), pts[:, 0].min(), pts[:, 0].max(), pts[:, 1].min(), pts[:, 1].max()))

# ---- 軸に沿った線分に分解 ----
# 隣り合う点の進む向きで、x 一定の面か z 一定の面かを決める
segs = []
i = 0
N = len(pts)
while i < N-12:
    # 12 点先までの主方向
    best = None
    for axis in (0, 1):      # 0: x 一定 (z 方向に伸びる面)  1: z 一定
        j = i+1
        val = pts[i, axis]
        while j < N:
            if abs(pts[j, axis]-val) > 0.35:
                break
            val = 0.75*val + 0.25*pts[j, axis]
            j += 1
        if best is None or j-i > best[1]-best[0]:
            best = (i, j, axis)
    i0, i1, axis = best
    if i1-i0 >= 14:
        sl = pts[i0:i1]
        off = float(np.median(sl[:, axis]))
        lo = float(np.percentile(sl[:, 1-axis], 2))
        hi = float(np.percentile(sl[:, 1-axis], 98))
        rms = float(np.std(sl[:, axis]))
        if hi-lo > 0.5:
            segs.append(dict(axis=int(axis), off=off, lo=lo, hi=hi, n=int(i1-i0),
                             col0=int(cols[i0]), col1=int(cols[i1-1]), rms=rms,
                             depth=float(np.median(np.hypot(sl[:, 0]-CAM[0], sl[:, 1]-CAM[2])))))
        i = i1
    else:
        i += 4

print('\n見えている壁面 %d 枚 (world 座標, m)' % len(segs))
print(' 画像列       面           範囲                長さ  点数  ばらつき  距離')
for s in segs:
    nm = ('x = %6.2f' % s['off']) if s['axis'] == 0 else ('z = %6.2f' % s['off'])
    rng = 'z %6.2f..%6.2f' % (s['lo'], s['hi']) if s['axis'] == 0 \
        else 'x %6.2f..%6.2f' % (s['lo'], s['hi'])
    print(' %4d-%4d  %s  %s  %5.2f m %4d  %6.3f m %6.1f m'
          % (s['col0'], s['col1'], nm, rng, s['hi']-s['lo'], s['n'], s['rms'], s['depth']))

json.dump(segs, open('artifacts/replica/plan_segments.json', 'w'), indent=1)

# ---- 平面図を描く ----
X0, X1, Z0, Z1, S = -2.0, 46.0, -2.0, 46.0, 21
nx, nz = int((X1-X0)*S), int((Z1-Z0)*S)
img = Image.new('RGB', (nx, nz), (24, 25, 28))
dr = ImageDraw.Draw(img)


def px(x, z):
    return ((x-X0)*S, nz-(z-Z0)*S)


for k in range(0, int(X1)+1, 6):
    dr.line([px(k, Z0), px(k, Z1)], fill=(56, 59, 68))
    dr.text((px(k, Z0)[0]+3, nz-15), '%d' % k, fill=(120, 126, 138))
for k in range(0, int(Z1)+1, 6):
    dr.line([px(X0, k), px(X1, k)], fill=(56, 59, 68))
    dr.text((4, px(X0, k)[1]-14), '%d' % k, fill=(120, 126, 138))
for p in pts:
    a = px(p[0], p[1])
    dr.point([a], fill=(150, 155, 168))
for s in segs:
    if s['axis'] == 0:
        a, b = px(s['off'], s['lo']), px(s['off'], s['hi'])
    else:
        a, b = px(s['lo'], s['off']), px(s['hi'], s['off'])
    dr.line([a, b], fill=(255, 200, 60), width=3)
cp = px(CAM[0], CAM[2])
dr.ellipse([cp[0]-6, cp[1]-6, cp[0]+6, cp[1]+6], fill=(88, 255, 160))
for sgn in (-1, 1):
    a = YAW + sgn*np.arctan(W/2.0/F)
    e = np.array([CAM[0], CAM[2]]) + np.array([np.sin(a), np.cos(a)])*60
    dr.line([cp, px(e[0], e[1])], fill=(88, 255, 160))
img.save('artifacts/replica/plan_extracted.png')
print('\n-> artifacts/replica/plan_extracted.png  /  plan_segments.json')
