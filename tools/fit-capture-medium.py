# -*- coding: utf-8 -*-
"""撮影媒体を、幾何が合った状態の生のレンダから測り直す。

前回の当てはめは間取りが合っていない状態で行ったため、k1 が間取りの誤差を
肩代わりしてしまっていた (k1 = -0.19)。間取りを取り合い線に対して 0.5px まで
詰めた今は、歪みを入れると逆に幾何が壊れる。ここでは
    k1 = 0 (ピンホール)
に固定し、記録系の 3 つ
    sigma_b   MTF に相当するガウス幅
    g, sig_g  ベーリンググレアの強さと広がり
    gain      露出
だけを当てはめる。

  python tools/fit-medium2.py artifacts/shots/R1_match_raw.png 1.194
"""
import sys, json
import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.optimize import minimize

REF = 'artifacts/reference/Backrooms_1.jpg'
RAW = sys.argv[1] if len(sys.argv) > 1 else 'artifacts/shots/R1_match_raw.png'
OVER = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0

FIT_W = 650
ref_full = Image.open(REF).convert('RGB')
FW, FH = ref_full.size
ref = ref_full.resize((FIT_W, round(FH*FIT_W/FW)), Image.LANCZOS)
W, H = ref.size
SC = W/1300.0
R = np.asarray(ref, np.float64)/255.
M0 = np.asarray(Image.open(RAW).convert('RGB').resize((W, H), Image.LANCZOS), np.float64)/255.


def srgb2lin(c):
    return np.where(c <= 0.04045, c/12.92, ((c+0.055)/1.055)**2.4)


def lin2srgb(c):
    c = np.clip(c, 0, None)
    return np.where(c <= 0.0031308, c*12.92, 1.055*c**(1/2.4)-0.055)


# オーバースキャンぶんだけ中心から拡大して、参照と同じ画角に切り出す
if abs(OVER-1.0) > 1e-6:
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float64)
    cx, cy = W/2.0, H/2.0
    sx = cx + (xx-cx)/OVER
    sy = cy + (yy-cy)/OVER
    M0 = np.stack([ndimage.map_coordinates(M0[..., c], [sy, sx], order=1, mode='nearest')
                   for c in range(3)], -1)
Ml = srgb2lin(M0)


def medium(p, img):
    """ベーリンググレアは加算。混合ではない。

    (1-g)*I + g*blur(I) と書くと元の画を (1-g) 倍に縮めるので、細部の
    コントラストまで一緒に落ちる。実際のグレアはレンズの中で散乱した光が
    「足される」ものなので、I + g*blur(I) が正しい。黒は浮き、
    明暗比は縮むが、細部の勾配は残る。"""
    sb, g, sg, gr, gg, gb = p
    o = img
    if sb > 0.03:
        o = ndimage.gaussian_filter(o, (sb, sb, 0))
    if g > 1e-4:
        o = o + g*ndimage.gaussian_filter(o, (sg, sg, 0))
    return o*np.array([gr, gg, gb])


def cost(p):
    d = lin2srgb(medium(p, Ml)) - R
    a = np.abs(d)
    return float(np.where(a < 0.05, 0.5*a*a/0.05, a-0.025).mean())


P0 = np.array([1.2*SC, 0.20, 40.0*SC, 0.75, 0.75, 0.75])
# グレアの上限を 0.30 に絞る。実在のレンズのベーリンググレアはこの程度で、
# それ以上必要になるのは「シーンの暗部が実際より暗い」ときだけ。
# 上限を開けておくと、照明の不足をグレアが肩代わりして測定にならない。
B = [(0.0, 4.0*SC), (0.0, 0.30), (4.0*SC, 120.0*SC),
     (0.55, 1.5), (0.55, 1.5), (0.55, 1.5)]
print('生のレンダ %s  (オーバースキャン %.3f を戻して比較)' % (RAW, OVER))
print('初期コスト %.5f' % cost(P0))
sol = minimize(cost, P0, method='Powell', bounds=B,
               options=dict(maxiter=14, xtol=1e-3, ftol=1e-4))
sb, g, sg, gr, gg, gb = sol.x
print('\n== 記録系の実測 ==')
print('  MTF のガウス幅   %.2f px (1920px 換算 %.2f)' % (sb/SC*1300/1300, sb/SC*1920/1300))
print('  グレア           強さ %.3f  広がり %.1f px (1920px 換算 %.1f)'
      % (g, sg/SC, sg/SC*1920/1300))
print('  チャンネルゲイン R %.4f  G %.4f  B %.4f  (輝度で %+.3f 段)'
      % (gr, gg, gb, np.log2(gr*.2126+gg*.7152+gb*.0722)))
print('  コスト           %.5f -> %.5f' % (cost(P0), sol.fun))

out = lin2srgb(medium(sol.x, Ml))
Image.fromarray((np.clip(out, 0, 1)*255).astype(np.uint8)).save(
    'artifacts/replica/mine_through_medium.png')


def st(a, nm):
    Y = a[..., 0]*.2126+a[..., 1]*.7152+a[..., 2]*.0722
    br = (a[..., 2]/np.maximum(a[..., 0], 1e-6)).mean()
    rg = (a[..., 0]/np.maximum(a[..., 1], 1e-6)).mean()
    gy_, gx_ = np.gradient(ndimage.gaussian_filter(Y, 0.6*SC/0.5))
    print('  %-14s 中央値 %.4f  p5 %.4f  p95 %.4f  p95/p5 %.2f  勾配 %.5f  R/G %.3f  B/R %.3f'
          % (nm, np.median(Y), np.percentile(Y, 5), np.percentile(Y, 95),
             np.percentile(Y, 95)/max(np.percentile(Y, 5), 1e-6),
             np.hypot(gx_, gy_).mean(), rg, br))


print('\n== 階調と解像感 ==')
st(R, '参照')
st(M0, 'レンダ (生)')
st(out, 'レンダ (媒体後)')
print('  平均絶対差  生 %.4f -> 媒体後 %.4f' % (np.abs(M0-R).mean(), np.abs(out-R).mean()))

json.dump(dict(k1=0.0, sigma_blur_1920=sb/SC*1920/1300, glare=g,
               sigma_glare_1920=sg/SC*1920/1300,
               gain_r=gr, gain_g=gg, gain_b=gb,
               overscan=OVER, cost=float(sol.fun)),
          open('artifacts/replica/medium.json', 'w'), indent=1)
print('\n-> artifacts/replica/medium.json')
