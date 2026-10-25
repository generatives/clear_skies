"""Tries off-the-shelf solvers on a settle problem written by the prototype (--solve --export FILE).

The same balance as Settle.Solve.cs: every cell's head such that rain plus flow in equals flow out (and into the void),
flow across a face its permeability times the head difference, times how wet the cell it comes from is (the higher
head's). Heads never go under a cell's bottom (there it's dry). Newton's method, held back by a pseudo-time step near
cells' wet edges, each step solved by GMRES with PyAMG's smoothed aggregation multigrid as the preconditioner.

    python solve.py land8.bin [--air 10,100] [--wet 0.1] [--smooth] [--rounds 200]
"""
import argparse, json, time
import numpy as np
import scipy.sparse as sp
import scipy.sparse.linalg as sla
import pyamg

ap = argparse.ArgumentParser()
ap.add_argument("file")
ap.add_argument("--air", default="0")
ap.add_argument("--wet", type=float, default=0.1)
ap.add_argument("--smooth", action="store_true", help="smoothstep wetness instead of linear")
ap.add_argument("--rounds", type=int, default=200)
ap.add_argument("--tol", type=float, default=0.01)
ap.add_argument("--linear", type=float, default=0.1, help="how far each linear solve brings its residual down")
ap.add_argument("--amg", default="sa", choices=["sa", "rs"], help="smoothed aggregation or classical Ruge-Stuben")
ap.add_argument("--save", default=None)
args = ap.parse_args()

with open(args.file, "rb") as f:
    head = json.loads(f.readline())
    n, m = head["cells"], head["faces"]
    def take(dtype, count): return np.frombuffer(f.read(np.dtype(dtype).itemsize * count), dtype=dtype)
    a, b = take("<i4", m).astype(np.int64), take("<i4", m).astype(np.int64)
    perm, solid, cap = take("<f4", n).astype(float), take("<f4", n).astype(float), take("<f4", n).astype(float)
    voids, y, column = take("<i4", n).astype(float), take("<i4", n), take("<i4", n)
    rain = take("<f8", n).copy()
size = head["size"]
bottom = size * y.astype(float)
target = bottom - size
rain_total = rain.sum()
print(f"{n:,} cells, {m:,} faces, {size}-block cells, rain {rain_total:.4g} a pass")

wet = size * args.wet

def kr(h, bot):
    p = np.clip((h - bot) / wet, 0, 1)
    return p * p * (3 - 2 * p) if args.smooth else p

def dkr(h, bot):
    p = (h - bot) / wet
    inside = (p >= 0) & (p < 1)
    return np.where(inside, (6 * p * (1 - p) if args.smooth else 1.0) / wet, 0.0)

def balance(h, k_face, k_void, slope):
    """What each cell gains; with slope, also the Jacobian of what it loses (sparse)."""
    dh = h[a] - h[b]
    mine = dh >= 0  # water goes a -> b
    up = np.where(mine, a, b)
    hu = h[up]
    k_up = kr(hu, bottom[up])
    q = k_face * k_up * dh  # flow from a to b
    gain = rain - np.bincount(a, q, n) + np.bincount(b, q, n)
    over = np.maximum(h - target, 0)
    kv = kr(h, bottom)
    drain = k_void * kv * over
    gain -= drain
    if not slope:
        return gain, drain.sum()
    d_up = dkr(hu, bottom[up])
    dq_da = k_face * (k_up + np.where(mine, d_up * dh, 0))
    dq_db = k_face * (-k_up + np.where(mine, 0, d_up * dh))
    diag = np.bincount(a, dq_da, n) - np.bincount(b, dq_db, n) + k_void * (kv + dkr(h, bottom) * over) * (h > target)
    rows = np.concatenate([a, b])
    cols = np.concatenate([b, a])
    vals = np.concatenate([dq_db, -dq_da])
    J = sp.csr_matrix((vals, (rows, cols)), shape=(n, n))
    return gain, drain.sum(), J, diag

h = bottom.copy()
for air in [float(x) for x in args.air.split(",")]:
    k = perm * (air / head["air"]) ** (1 - solid) if air > 0 else perm
    k_face = np.minimum(k[a], k[b]) / size
    k_void = voids * k / size
    print(f"open air passes {air if air > 0 else head['air']}")
    t0 = time.time()
    tau = 1.0
    gain, drained = balance(h, k_face, k_void, False)
    off = np.abs(gain).sum() / rain_total
    total_its = 0
    for rnd in range(1, args.rounds + 1):
        lap = time.time()
        if off < args.tol:
            print(f"round {rnd}: off balance {off:.4f}, drained/rained {drained / rain_total:.3f} - settled")
            break
        gain, drained, J, diag = balance(h, k_face, k_void, True)
        edge = (h - bottom) < 2 * wet
        held = np.where(edge, diag * (1 + 1 / tau) + 1e-4 * cap / size / tau, diag)
        A = (J + sp.diags(held)).tocsr()
        t_setup = time.time()
        if args.amg == "sa":
            ml = pyamg.smoothed_aggregation_solver(A, symmetry="nonsymmetric", max_coarse=500)
        else:
            ml = pyamg.ruge_stuben_solver(A, max_coarse=500)
        t_setup = time.time() - t_setup
        its = [0]
        step, info = sla.gmres(A, gain, rtol=args.linear, restart=40, maxiter=3, M=ml.aspreconditioner(),
                               callback=lambda r: its.__setitem__(0, its[0] + 1), callback_type="pr_norm")
        total_its += its[0]
        lam, nxt, taken = 1.0, None, False
        for _ in range(3):
            reach = np.minimum(size, np.maximum(wet, np.abs(h - bottom)))
            trial = np.maximum(bottom, h + lam * np.clip(step, -reach, reach))
            g2, d2 = balance(trial, k_face, k_void, False)
            nxt = np.abs(g2).sum() / rain_total
            if nxt < off:
                break
            lam *= 0.5
        if nxt < 1.5 * off:
            h, gain, drained, taken = trial, g2, d2, True
            tau = min(1e12, tau * (2 if nxt < off and lam == 1 else 1 if lam == 1 else 0.5))
            off = nxt
        else:
            tau = max(1e-4, tau / 4)
        print(f"round {rnd}: off balance {off:.4f}, drained/rained {drained / rain_total:.3f}, tau {tau:.3g}, "
              f"{its[0]} GMRES steps, AMG setup {t_setup:.2f} s, {ml.levels.__len__()} levels"
              f"{'' if taken else ', not taken'}{f', {lam} of it' if taken and lam < 1 else ''}, {time.time() - lap:.2f} s")
    print(f"solved in {time.time() - t0:.1f} s, {total_its} GMRES steps in all")

open_blocks = np.maximum(0, np.minimum(h, bottom + size) - (bottom + size * solid))
cols = np.bincount(column, open_blocks, head["nx"] * head["nz"])
land = np.bincount(column, np.ones(n), head["nx"] * head["nz"]) > 0
d = cols[land]
print("open water over land columns: " + ", ".join(
    f"{name} {100 * np.mean(sel):.0f}%" for name, sel in
    [("none", d <= 0.01), ("under 1/4", (d > 0.01) & (d < 0.25)), ("to 1", (d >= 0.25) & (d < 1)),
     ("1-4", (d >= 1) & (d < 4)), ("4-16", (d >= 4) & (d < 16)), ("deeper", d >= 16)]))
if args.save:
    np.save(args.save, h)
