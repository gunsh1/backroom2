# -*- coding: utf-8 -*-
"""参照画像と、それを複製したレンダを「構造」で突き合わせる。

これまでの compare-reference.py は色と階調しか比べていない。構図が違う
2 枚の間では、それしか比べようがなかったから。参照画像を実測して同じ構造を
組み直した (Level0Replica) 以上、ここでは画素の位置そのものを比べられる。

一番強い証拠は天井の照明の位置。器具は天井面上の規則格子なので、
  * カメラの位置・向き・画角
  * 天井高
  * 器具のピッチ
のどれか一つでも違えば、画面上の位置は必ずずれる。逆に全部合っていれば
数画素以内で重なる。壁と床の取り合い線も同様。

  python tools/compare-replica.py artifacts/shots/R1_match.png
"""
import sys, json
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

REF = 'artifacts/reference/Backrooms_1.jpg'
MINE = sys.argv[1] if len(sys.argv) > 1 else 'artifacts/shots/R1_match.png'
OUT = 'artifacts/replica'


def load(path, size):
    im = Image.open(path).convert('RGB')
    if im.size != size:
        im = im.resize(size, Image.LANCZOS)
    return im


def lum(a):
    return a[..., 0]*0.2126 + a[..., 1]*0.7152 + a[..., 2]*0.0722


def fixtures(Y, thr=0.80, minarea=60):
    """天井の照明を拾う。返すのは (cx, cy, 面積)。"""
    lab, _ = ndimage.label(Y > thr)
    out = []
    for i, sl in enumerate(ndimage.find_objects(lab), start=1):
        sub = (lab[sl] == i)
        n = int(sub.sum())
        if n < minarea:
            continue
        ys, xs = np.nonzero(sub)
        out.append((xs.mean()+sl[1].start, ys.mean()+sl[0].start, n))
    out.sort(key=lambda t: -t[2])
    return out


def match(a, b, tol=60.0):
    """近い順に 1 対 1 で対応づける。"""
    pairs, used = [], set()
    for i, (ax, ay, aa) in enumerate(a):
        best, bd = None, 1e9
        for j, (bx, by, ba) in enumerate(b):
            if j in used:
                continue
            d = np.hypot(ax-bx, ay-by)
            if d < bd:
                best, bd = j, d
        if best is not None and bd < tol:
            used.add(best)
            pairs.append((i, best, bd))
    return pairs


def base_line(Y, x0, x1, y_lo=400, guide=None, win=70):
    """指定した列の範囲で、壁と床の取り合い (下向きの強い輝度段差) を追う。

    guide を渡すと、その直線の上下 win 画素だけを探す。これが要る理由:
    こちらのレンダはカーペットが 10m 以遠で平均色に潰れ、その境界が
    壁の取り合いより強い輝度段差になる。画面全体から最強の段差を採ると
    床の模様を壁だと誤って読む。
    """
    Ys = ndimage.gaussian_filter(Y, 1.8)
    gy = np.gradient(Ys, axis=0)
    H = Y.shape[0]
    X, V = [], []
    for x in range(x0, x1):
        lo, hi = y_lo, H-4
        if guide is not None:
            c = np.polyval(guide, x)
            lo, hi = max(y_lo, int(c-win)), min(H-4, int(c+win))
            if hi-lo < 8:
                continue
        col = -gy[lo:hi, max(0, x-1):x+2].mean(1)
        k = int(np.argmax(col))
        if col[k] < 0.006:
            continue
        y_lo_ = lo
        a_, b_, c_ = col[max(k-1, 0)], col[k], col[min(k+1, len(col)-1)]
        d = 0.5*(a_-c_)/(a_-2*b_+c_) if (a_-2*b_+c_) != 0 else 0
        X.append(float(x)); V.append(y_lo_+k+d)
    if len(X) < 20:
        return None
    X, V = np.array(X), np.array(V)
    for _ in range(4):
        p = np.polyfit(X, V, 1); r = np.abs(V-np.polyval(p, X))
        m = r < 2.0*max(r.std(), 0.4); X, V = X[m], V[m]
    return np.polyfit(X, V, 1), float(np.std(V-np.polyval(np.polyfit(X, V, 1), X)))


def main():
    ref = load(REF, (1300, 731))
    W, H = ref.size
    mine = load(MINE, (W, H))
    ra = np.asarray(ref, np.float64)/255.
    ma = np.asarray(mine, np.float64)/255.
    rY, mY = lum(ra), lum(ma)

    print('=' * 74)
    print('  参照            : %s' % REF)
    print('  複製のレンダ    : %s' % MINE)
    print('=' * 74)

    # ---------------------------------------------------------- 1. 照明の位置
    rf, mf = fixtures(rY), fixtures(mY)
    pairs = match(rf, mf)
    print('\n■ 天井の照明の画面上の位置  (構造が合っているかの直接の証拠)')
    print('   検出台数        参照 %d 台 / 複製 %d 台' % (len(rf), len(mf)))
    if pairs:
        d = np.array([p[2] for p in pairs])
        print('   対応がついた数  %d 台' % len(pairs))
        print('   ずれ            中央値 %.1f px   平均 %.1f px   最大 %.1f px'
              % (np.median(d), d.mean(), d.max()))
        print('                   画面の対角 %.0f px に対して %.2f %%'
              % (np.hypot(W, H), np.median(d)/np.hypot(W, H)*100))
        print('   大きい器具から 10 台 (参照 -> 複製):')
        for (i, j, dd) in pairs[:10]:
            print('      (%6.1f,%6.1f) -> (%6.1f,%6.1f)   %5.1f px   面積 %5d / %5d'
                  % (rf[i][0], rf[i][1], mf[j][0], mf[j][1], dd, rf[i][2], mf[j][2]))
    else:
        print('   対応づけ不能')

    # ------------------------------------------------- 2. 壁と床の取り合い線
    print('\n■ 壁と床の取り合い線  (間取りが合っているか)')
    SEG = [(4, 151), (343, 533), (569, 659), (732, 821), (940, 1064), (1065, 1262)]
    for (x0, x1) in SEG:
        r = base_line(rY, x0, x1)
        if r is None:
            print('   x %4d-%4d   参照側で検出できず' % (x0, x1)); continue
        m = base_line(mY, x0, x1, guide=r[0])
        if m is None:
            print('   x %4d-%4d   複製側で検出できず (参照の線の +-70px 以内に段差なし)' % (x0, x1)); continue
        xm = (x0+x1)/2
        yr, ym = np.polyval(r[0], xm), np.polyval(m[0], xm)
        print('   x %4d-%4d   y  参照 %6.1f / 複製 %6.1f  (差 %+5.1f px)   '
              '傾き %+.4f / %+.4f' % (x0, x1, yr, ym, ym-yr, r[0][0], m[0][0]))

    # ------------------------------------------------------------ 3. 階調
    print('\n■ 階調 (全画素)')
    rows = [('中央値 Y', np.median(rY), np.median(mY)),
            ('p5', np.percentile(rY, 5), np.percentile(mY, 5)),
            ('p95', np.percentile(rY, 95), np.percentile(mY, 95)),
            ('p95/p5', np.percentile(rY, 95)/max(np.percentile(rY, 5), 1e-6),
             np.percentile(mY, 95)/max(np.percentile(mY, 5), 1e-6)),
            ('平均 R/G', (ra[..., 0]/np.maximum(ra[..., 1], 1e-6)).mean(),
             (ma[..., 0]/np.maximum(ma[..., 1], 1e-6)).mean()),
            ('平均 B/R', (ra[..., 2]/np.maximum(ra[..., 0], 1e-6)).mean(),
             (ma[..., 2]/np.maximum(ma[..., 0], 1e-6)).mean()),
            ('白飛び %', (rY > 0.995).mean()*100, (mY > 0.995).mean()*100)]
    print('   %-12s %10s %10s %10s' % ('', '参照', '複製', '差'))
    for nm, a_, b_ in rows:
        print('   %-12s %10.4f %10.4f %+10.4f' % (nm, a_, b_, b_-a_))

    # ------------------------------------------------ 3b. 面ごとの明るさ
    def surf(a):
        Yl = lum(a)
        lb, _ = ndimage.label(Yl > 0.80)
        if lb.max() == 0:
            return None, None
        sz = ndimage.sum(np.ones_like(Yl), lb, range(1, lb.max()+1))
        keep = np.isin(lb, 1+np.nonzero(sz >= 60)[0])
        ceil = (~keep) & (Yl > 0.30) & (Yl < 0.80)
        ceil[Yl.shape[0]//2:, :] = False
        return Yl[keep], Yl[ceil]

    rl, rc = surf(ra)
    ml, mc = surf(ma)
    print()
    print('■ 器具のレンズと天井タイルの明るさ')
    if rl is None or ml is None:
        print('   レンズが検出できず')
    else:
        print('   %-14s %10s %10s' % ('', '参照', '複製'))
        print('   %-14s %10.4f %10.4f' % ('レンズ 平均', rl.mean(), ml.mean()))
        print('   %-14s %9.2f%% %9.2f%%' % ('レンズ 飽和', (rl > 0.995).mean()*100,
                                            (ml > 0.995).mean()*100))
        print('   %-14s %10.4f %10.4f' % ('天井タイル', rc.mean(), mc.mean()))
        print('   %-14s %10.2f %10.2f  段' % ('レンズ/天井',
                                              np.log2(rl.mean()/rc.mean()),
                                              np.log2(ml.mean()/mc.mean())))

    # ------------------------------------------------ 3c. 解像感
    def sharp(a):
        Yl = lum(a)
        gy_, gx_ = np.gradient(ndimage.gaussian_filter(Yl, 0.6))
        return float(np.sqrt(gx_**2 + gy_**2).mean())
    print()
    print('■ 解像感 (勾配の平均)')
    print('   参照 %.5f   複製 %.5f   比 %.2f 倍'
          % (sharp(ra), sharp(ma), sharp(ma)/max(sharp(ra), 1e-9)))

    # ------------------------------------------------ 3d. 面ごとの明るさ
    # 構図が揃っているので、同じ画素範囲が同じ面を指す。どの材質が
    # ずれているかはここでしか分からない。
    REG = [('右の塊の壁面', 1100, 1280, 300, 420),
           ('右の塊の幅木', 1100, 1260, 548, 566),
           ('手前のカーペット', 300, 900, 640, 720),
           ('天井 (左上)', 200, 600, 30, 120),
           ('天井 (中央)', 700, 900, 20, 140),
           ('中景の壁 (左)', 380, 520, 360, 430)]
    print()
    print('■ 面ごとの明るさ  (同じ画素範囲 = 同じ面)')
    print('   %-16s %8s %8s %8s   %s' % ('', '参照', '複製', '差', 'B/R の差'))
    for nm, x0, x1, y0, y1 in REG:
        p = ra[y0:y1, x0:x1].reshape(-1, 3)
        q = ma[y0:y1, x0:x1].reshape(-1, 3)
        yp = (p @ [.2126, .7152, .0722]).mean()
        yq = (q @ [.2126, .7152, .0722]).mean()
        bp = (p[:, 2]/np.maximum(p[:, 0], 1e-6)).mean()
        bq = (q[:, 2]/np.maximum(q[:, 0], 1e-6)).mean()
        print('   %-16s %8.3f %8.3f %+8.3f   %+.3f' % (nm, yp, yq, yq-yp, bq-bp))

    # ---------------------------------------------- 4. 画像を並べて書き出す
    sbs = Image.new('RGB', (W, H*2+8), (20, 20, 20))
    sbs.paste(ref, (0, 0)); sbs.paste(mine, (0, H+8))
    sbs.save(OUT + '/side_by_side.png')

    # 参照の輪郭を複製の上に重ねる
    ov = mine.copy()
    g = ndimage.gaussian_filter(rY, 1.2)
    gy_, gx_ = np.gradient(g)
    e = np.hypot(gx_, gy_)
    e = e > np.quantile(e, 0.985)
    arr = np.asarray(ov).copy()
    arr[e] = [255, 40, 40]
    Image.fromarray(arr).save(OUT + '/overlay_edges.png')

    # 市松に合成すると、線のつながり方でずれが読める
    chk = np.asarray(mine).copy()
    bs = 64
    yy, xx = np.mgrid[0:H, 0:W]
    m = ((yy//bs + xx//bs) % 2) == 0
    chk[m] = np.asarray(ref)[m]
    Image.fromarray(chk).save(OUT + '/checker.png')

    d = np.abs(rY-mY)
    Image.fromarray((np.clip(d*3, 0, 1)*255).astype(np.uint8)).save(OUT + '/absdiff.png')
    print('\n   -> %s/side_by_side.png  overlay_edges.png  checker.png  absdiff.png' % OUT)
    print('   輝度の絶対差 平均 %.4f  中央値 %.4f' % (d.mean(), np.median(d)))


main()
