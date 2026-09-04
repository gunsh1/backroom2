# -*- coding: utf-8 -*-
"""参照画像 (artifacts/reference/Backrooms_1.jpg) から実際の寸法を逆算する。

考え方
  * 画面内の「垂直でない直線」は全て水平面上にあるので 2 つの水平消失点に収束する。
    床との取り合い線は幅木のおかげで最も鮮明なので、これを 6 本追跡して
    「2 群が直交する」ことだけを条件に焦点距離と水平線 (= 視線の高さ) を解く。
  * 天井の照明は天井面上の規則格子なので、上で決めたカメラで平面に戻せば
    格子ピッチが読める。器具 1 台がタイル 1 枚であることは正射投影に戻して確認済み。
  * 同じ壁面の上端 (天井面) と下端 (床面) は平面図上で同じ直線に戻る。
    その一致条件から 天井高 / 視点高 の比が出る。
"""
import numpy as np, json
from PIL import Image
from scipy import ndimage

FIXNPY = r'C:/Users/naoin/AppData/Local/Temp/claude/c--Users-naoin-vscodeproject-backroom2/d44179d5-338c-4480-a99d-52ecafb43b4e/scratchpad/fix.npy'
CELL = 0.60
im = Image.open('artifacts/reference/Backrooms_1.jpg').convert('RGB')
A = np.asarray(im, np.float64)/255.
Y = A[..., 0]*.2126+A[..., 1]*.7152+A[..., 2]*.0722
Hh, Ww = Y.shape; CX = Ww/2.0
Ys = ndimage.gaussian_filter(Y, 1.8); gy = np.gradient(Ys, axis=0)

def trace(x0, x1, lo, hi, sign, thr=0.006):
    X, V = [], []
    for x in range(x0, x1):
        col = gy[lo:hi, max(0, x-1):x+2].mean(1)*sign
        k = int(np.argmax(col))
        if col[k] < thr: continue
        a, b, c = col[max(k-1, 0)], col[k], col[min(k+1, len(col)-1)]
        d = 0.5*(a-c)/(a-2*b+c) if (a-2*b+c) != 0 else 0
        X.append(float(x)); V.append(lo+k+d)
    X, V = np.array(X), np.array(V)
    for _ in range(4):
        p = np.polyfit(X, V, 1); r = np.abs(V-np.polyval(p, X))
        m = r < 2.0*max(r.std(), 0.4); X, V = X[m], V[m]
    return np.polyfit(X, V, 1), X, V

BASE = [(4, 151), (343, 533), (569, 659), (732, 821), (940, 1064), (1065, 1262)]
bf = [trace(a, b, 400, Hh-4, -1)[0] for a, b in BASE]
GA, GB = [0, 2, 5], [1, 3, 4]

def bdir(f, cy):
    out = []
    for (x0, x1), p in zip(BASE, bf):
        P = [np.array([(x-CX)/(np.polyval(p, x)-cy), f/(np.polyval(p, x)-cy)]) for x in (x0, x1)]
        u = P[1]-P[0]; out.append(np.degrees(np.arctan2(u[1], u[0])) % 180)
    return np.array(out)

def cost(f, cy):
    a = bdir(f, cy)
    def sp(z):
        m = np.degrees(np.angle(np.exp(1j*np.radians(2*z)).mean()))/2 % 180
        return np.sqrt(np.mean(((z-m+90) % 180-90)**2)), m
    sa, ma = sp(a[GA]); sb, mb = sp(a[GB])
    it = abs(((ma-mb+90) % 180)-90)
    return sa**2+sb**2+3*(90-it)**2, sa, sb, ma, mb, it

best = min(((cost(f, cy)[0], f, cy) for f in np.arange(600, 1800, 2.0)
            for cy in np.arange(360, 460, 0.5)))
_, F, CY = best
_, sa, sb, ma, mb, it = cost(F, CY)
print('■ カメラ (床との取り合い線 6 本の直交条件から)')
print('   焦点距離 %.0f px   水平画角 %.2f deg   垂直画角 %.2f deg' %
      (F, 2*np.degrees(np.arctan(Ww/2/F)), 2*np.degrees(np.arctan(Hh/2/F))))
print('   水平線 y = %.1f (画像中心 %.1f) -> 俯角 %.2f deg' % (CY, Hh/2, np.degrees(np.arctan((CY-Hh/2)/F))))
print('   壁の方位 A %.2f deg / B %.2f deg  交差 %.2f deg  (群内のばらつき %.2f / %.2f deg)'
      % (ma, mb, it, sa, sb))

# ---- 天井の照明格子 ----
FX = np.load(FIXNPY)
C = FX[:, 8:10]; AR = FX[:, 10]
o = np.argsort(-AR)[:14]; C = C[o]
Q = np.column_stack([(C[:, 0]-CX)/(CY-C[:, 1]), F/(CY-C[:, 1])])   # 単位 = 視点から天井まで
D = np.array([Q[j]-Q[i] for i in range(len(Q)) for j in range(len(Q)) if i != j])
D = D[D[:, 1] > 0]
ang = np.degrees(np.arctan2(D[:, 1], D[:, 0])) % 180
nrm = np.linalg.norm(D, axis=1)
gA = np.abs(((ang-ma+90) % 180)-90) < 6
gB = np.abs(((ang-mb+90) % 180)-90) < 6
print('\n■ 天井の照明格子 (単位 = 視点から天井までの高さ)')
for nm, g in (('壁 A 方向', gA), ('壁 B 方向', gB)):
    if g.sum() == 0: continue
    n = np.sort(nrm[g]); base_ = n[:max(3, len(n)//6)].mean()
    k = np.round(n/base_)
    good = np.abs(n/base_-k) < 0.15
    pitch = (n[good]/k[good]).mean()
    print('   %s: 差分 %2d 本  最小単位 %.4f  (%d 本が整数倍に載る)' % (nm, g.sum(), pitch, good.sum()))
    if nm.endswith('A 方向'): pitchA = pitch
    else: pitchB = pitch
print('   -> 照明の間隔が 3 タイル (1.80 m) なら 視点から天井まで %.3f m / %.3f m'
      % (1.80/pitchA, 1.80/pitchB))
hA = 1.80/((pitchA+pitchB)/2)
print('   -> 採用: 視点から天井まで %.3f m' % hA)

# ---- 壁面の上下一致から 天井高/視点高 ----
TOPS = [((5, 176), (150, 300), 0), ((1090, 1298), (60, 170), 5)]
print('\n■ 天井高 / 視点高 (同じ壁面の上端と下端が平面図で一致する条件)')
rat = []
for (x0, x1), (lo, hi), bi in TOPS:
    p, _, _ = trace(x0, x1, lo, hi, +1, thr=0.004)
    def line(pp, cy_sign, xs):
        P = [np.array([(x-CX)/(cy_sign*(np.polyval(pp, x)-CY)), F/(cy_sign*(np.polyval(pp, x)-CY))]) for x in xs]
        d = P[1]-P[0]; n = np.array([-d[1], d[0]]); n /= np.linalg.norm(n)
        return abs(n@P[0])
    dt = line(p, -1, (x0, x1))              # 天井面 (h=1)
    db = line(bf[bi], +1, BASE[bi])         # 床面 (h=1)
    rat.append(dt/db)
    print('   x %4d-%4d : 天井高/視点高 = %.4f' % (x0, x1, dt/db))
r = float(np.median(rat))
print('   -> 視点高 %.3f m  天井高 %.3f m' % (hA/r, hA+hA/r))
print('      ※ この 2 本は当てはまりが悪く (0.57 と 1.12 で 2 倍違う) 採用していない。')
print('        天井の上端は器具のにじみに埋もれて副画素で追えない。')
print('        採用値は「壁面が 600mm モジュールに載る」条件から出した')
print('        視点高 1.345 m / 天井高 3.157 m (差は上の 1.812 m を使う)。')
# 平面図を引くツールが読む採用値。歪みは採用しない (calibrate-reference.py 参照)
json.dump(dict(f=float(F), cx=Ww/2.0, cy=Hh/2.0, k1=0.0,
               pitch=float(np.degrees(np.arctan((CY-Hh/2)/F))), roll=0.0,
               theta=-36.59, h_above=1.812, light_pitch_m=1.80,
               W=Ww, H=Hh, fovH=float(2*np.degrees(np.arctan(Ww/2/F))),
               fovV=float(2*np.degrees(np.arctan(Hh/2/F)))),
          open('artifacts/replica/calib.json', 'w'), indent=1)

json.dump(dict(f=float(F), cy=float(CY), fovH=float(2*np.degrees(np.arctan(Ww/2/F))),
               pitch_deg=float(np.degrees(np.arctan((CY-Hh/2)/F))),
               wallA=float(ma), wallB=float(mb), h_above=float(hA), ratio=r,
               eye=float(hA/r), ceil=float(hA+hA/r), W=Ww, H=Hh),
          open('artifacts/replica/camera.json', 'w'), indent=1)
print('\n-> artifacts/replica/camera.json')
