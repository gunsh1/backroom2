# -*- coding: utf-8 -*-
"""参照画像のカメラを、レンズの歪みまで含めて較正する。

天井の照明は天井面上の規則格子なので、これは平面キャリブレーションそのもの。
ただし平面 1 枚では内部パラメータを全部自由にすると条件が悪い (焦点距離と
主点とピッチが互いに化ける)。主点は画像中心に固定し、歪みは k1 だけを許す。

未知数は f, pitch, roll, k1, 格子の間隔 P, 向き theta, 原点 Ox/Oz の 8 個。
観測は器具の中心 27 点 x 2 = 54。天井高を 1 とする単位で解き、実寸は
「器具の間隔が 3 タイル = 1.80m」から後で掛ける。

  python tools/calibrate-reference.py
"""
import json
import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.optimize import least_squares

SRC = 'artifacts/reference/Backrooms_1.jpg'
LIGHT_PITCH_M = 1.80          # 3 タイル。正射投影に戻して数えた

im = Image.open(SRC).convert('RGB')
W, H = im.size
CX, CY = W/2.0, H/2.0
A = np.asarray(im, np.float64) / 255.
Y = A[..., 0]*.2126 + A[..., 1]*.7152 + A[..., 2]*.0722

lab, _ = ndimage.label(Y > 0.80)
pts, area = [], []
for i, sl in enumerate(ndimage.find_objects(lab), start=1):
    sub = (lab[sl] == i)
    n = int(sub.sum())
    if n < 60:
        continue
    ys, xs = np.nonzero(sub)
    x0, x1 = xs.min()+sl[1].start, xs.max()+sl[1].start
    y0, y1 = ys.min()+sl[0].start, ys.max()+sl[0].start
    if x0 <= 1 or x1 >= W-2 or y0 <= 1 or y1 >= H-2:
        continue          # 画面の縁で切れている器具は重心が偏る
    pts.append((xs.mean()+sl[1].start, ys.mean()+sl[0].start))
    area.append(n)
pts = np.array(pts); area = np.array(area, float)
o = np.argsort(-area); pts, area = pts[o], area[o]
Wt = np.sqrt(area/area.max())
print('器具 %d 台 (画面の縁で切れているものは除外)' % len(pts))

# q = [f, pitch, roll, k1, P, theta, Ox, Oz]


def forward(q, mn):
    f, pitch, roll, k1, P, th, Ox, Oz = q
    c, s = np.cos(th), np.sin(th)
    X = Ox + P*(mn[:, 0]*c - mn[:, 1]*s)
    Z = Oz + P*(mn[:, 0]*s + mn[:, 1]*c)
    Yv = -np.ones_like(X)
    cp, sp = np.cos(pitch), np.sin(pitch)
    Yc = Yv*cp - Z*sp
    Zc = Yv*sp + Z*cp
    cr, sr = np.cos(roll), np.sin(roll)
    xn = (X*cr - Yc*sr)/Zc
    yn = (X*sr + Yc*cr)/Zc
    r2 = xn*xn + yn*yn
    d = 1.0 + k1*r2
    return np.column_stack([CX + f*xn*d, CY + f*yn*d])


def to_plane(q, p):
    """画像座標 -> 天井平面 (歪みを外してから)。"""
    f, pitch, roll, k1, P, th, Ox, Oz = q
    xn = (p[:, 0]-CX)/f; yn = (p[:, 1]-CY)/f
    for _ in range(8):
        r2 = xn*xn + yn*yn
        d = 1.0 + k1*r2
        xn = (p[:, 0]-CX)/f/d
        yn = (p[:, 1]-CY)/f/d
    cr, sr = np.cos(-roll), np.sin(-roll)
    xr = xn*cr - yn*sr; yr = xn*sr + yn*cr
    cp, sp = np.cos(pitch), np.sin(pitch)
    Z = (cp - yr*sp) / (-yr*cp - sp)
    Zc = -sp + Z*cp
    X = xr*Zc
    return np.column_stack([X, Z])


def labels(q):
    Q = to_plane(q, pts)
    c, s = np.cos(-q[5]), np.sin(-q[5])
    dx, dz = Q[:, 0]-q[6], Q[:, 1]-q[7]
    return np.column_stack([(dx*c - dz*s)/q[4], (dx*s + dz*c)/q[4]])


q0 = np.array([996.0, np.radians(2.27), 0.0, 0.0,
               0.993, np.radians(-36.59), 0.0, 0.0])
P0 = to_plane(q0, pts[:1])[0]
q0[6], q0[7] = P0[0], P0[1]
L0 = labels(q0)
MN = np.round(L0)
resid0 = np.abs(L0 - MN).max(1)
print('初期割り当て: 格子から %.3f 以内 %d 台 / %d 台'
      % (0.30, (resid0 < 0.30).sum(), len(pts)))
uniq = len(set(map(tuple, MN[resid0 < 0.30])))
print('             重複のない指数 %d 個' % uniq)

LO = np.array([600., np.radians(-14), np.radians(-6), -0.40, 0.70, -np.pi, -20., 0.5])
HI = np.array([1800., np.radians(14), np.radians(6), 0.40, 1.40, np.pi, 20., 40.])


def solve(free_k1):
    q = q0.copy()
    mn = MN.copy()
    keep = resid0 < 0.30
    for it in range(10):
        def res(qq):
            qq = np.array(qq)
            if not free_k1:
                qq[3] = 0.0
            return ((forward(qq, mn[keep]) - pts[keep]) * Wt[keep, None]).ravel()
        sol = least_squares(res, q, bounds=(LO, HI), loss='soft_l1', f_scale=2.5,
                            max_nfev=8000)
        q = sol.x
        if not free_k1:
            q[3] = 0.0
        L = labels(q)
        mn = np.round(L)
        keep = np.abs(L - mn).max(1) < 0.30
        if keep.sum() < 8:
            break
    e = np.linalg.norm(forward(q, mn[keep]) - pts[keep], axis=1)
    return q, keep, mn, e


out = {}
for name, fk in (('歪みなし (ピンホール)', False), ('放射歪み k1 あり', True)):
    q, keep, mn, e = solve(fk)
    f, pitch, roll, k1, P, th, Ox, Oz = q
    print('\n== %s ==' % name)
    print('  採用 %d / %d 台   残差 RMS %.2f px  中央値 %.2f  最大 %.2f'
          % (keep.sum(), len(pts), np.sqrt((e**2).mean()), np.median(e), e.max()))
    print('  f = %.1f px   水平画角 %.2f deg   垂直画角 %.2f deg'
          % (f, 2*np.degrees(np.arctan(W/2/f)), 2*np.degrees(np.arctan(H/2/f))))
    print('  ピッチ %+.3f deg (上向きが正)   ロール %+.3f deg'
          % (np.degrees(pitch), np.degrees(roll)))
    print('  k1 = %+.5f' % k1)
    print('  照明格子 間隔 %.4f (天井高=1)   向き %.2f deg' % (P, np.degrees(th)))
    print('  -> 視点から天井まで %.4f m' % (LIGHT_PITCH_M/P))
    rc = np.hypot(W/2, H/2)/f
    shift = rc*k1*rc**2*f
    print('  -> 画面の隅で %+.1f px 動く (%s)'
          % (shift, '樽' if shift < 0 else ('糸巻き' if shift > 0 else 'なし')))
    out[name] = dict(f=f, pitch=np.degrees(pitch), roll=np.degrees(roll), k1=k1,
                     P=P, theta=np.degrees(th), Ox=Ox, Oz=Oz,
                     h_above=LIGHT_PITCH_M/P,
                     fovH=2*np.degrees(np.arctan(W/2/f)),
                     fovV=2*np.degrees(np.arctan(H/2/f)),
                     rms=float(np.sqrt((e**2).mean())), used=int(keep.sum()),
                     corner_shift_px=float(shift))

best = out['放射歪み k1 あり']
best.update(W=W, H=H, cx=CX, cy=CY, light_pitch_m=LIGHT_PITCH_M)
json.dump(best, open('artifacts/replica/calib.json', 'w'), indent=1)
print('\n-> artifacts/replica/calib.json')
