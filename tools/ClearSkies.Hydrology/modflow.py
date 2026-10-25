"""Runs MODFLOW 6 on a settle problem written by the prototype (--solve --export FILE), as a benchmark: the same cells
(a DISU grid, one MODFLOW cell per kept cell), permeability as hydraulic conductivity, rain as recharge on the cell it
lands on, faces into the void as drains, steady state, Newton with MODFLOW's own wet/dry handling.

Units are blocks and passes. Our flow across a face is k * dh / size cells of water a pass, size^3 blocks each; MODFLOW's
is K * area * dh / length = K * size * dh, so K = k * size. Rain is cells of water a pass on a column: rain * size
blocks deep.

    python modflow.py land8.bin --mf6 /path/to/mf6 [--air 10] [--work dir]
"""
import argparse, json, os, time
import numpy as np
import flopy

ap = argparse.ArgumentParser()
ap.add_argument("file")
ap.add_argument("--mf6", required=True)
ap.add_argument("--air", type=float, default=0)
ap.add_argument("--work", default="mf")
ap.add_argument("--outer", type=int, default=500)
ap.add_argument("--complexity", default="COMPLEX")
args = ap.parse_args()

with open(args.file, "rb") as f:
    head = json.loads(f.readline())
    n, m = head["cells"], head["faces"]
    def take(dtype, count): return np.frombuffer(f.read(np.dtype(dtype).itemsize * count), dtype=dtype)
    a, b = take("<i4", m).astype(np.int64), take("<i4", m).astype(np.int64)
    perm, solid, cap = take("<f4", n).astype(float), take("<f4", n).astype(float), take("<f4", n).astype(float)
    voids, y, column = take("<i4", n).astype(float), take("<i4", n), take("<i4", n)
    rain = take("<f8", n).copy()
# MODFLOW wants a column's cells numbered top down (ours go bottom up).
perm_order = np.lexsort((-y, column))
renum = np.empty(n, dtype=np.int64); renum[perm_order] = np.arange(n)
a, b = renum[a], renum[b]
perm, solid, cap, voids, y, column, rain = (v[perm_order] for v in (perm, solid, cap, voids, y, column, rain))
size = float(head["size"])
k = perm * (args.air / head["air"]) ** (1 - solid) if args.air > 0 else perm
K = k * size
bottom = size * y.astype(float)
print(f"{n:,} cells, {m:,} faces, {size:g}-block cells, K from {K.min():.3g} to {K.max():.3g}")

# Connections, each cell's own first (DISU's layout).
vertical = column[a] == column[b]
src = np.concatenate([a, b]); dst = np.concatenate([b, a]); vert = np.concatenate([vertical, vertical])
order = np.lexsort((dst, src))
src, dst, vert = src[order], dst[order], vert[order]
counts = np.bincount(src, minlength=n)
iac = counts + 1
nja = int(iac.sum())
ja = np.empty(nja, dtype=np.int64); ihc = np.empty(nja, dtype=np.int64)
cl12 = np.empty(nja); hwva = np.empty(nja)
starts = np.concatenate([[0], np.cumsum(iac)[:-1]])
ja[starts] = np.arange(n); ihc[starts] = 1; cl12[starts] = 0; hwva[starts] = 0
mask = np.ones(nja, dtype=bool); mask[starts] = False
ja[mask] = dst
ihc[mask] = np.where(vert, 0, 1)
cl12[mask] = size / 2
hwva[mask] = np.where(vert, size * size, size)

os.makedirs(args.work, exist_ok=True)
sim = flopy.mf6.MFSimulation(sim_name="settle", exe_name=args.mf6, sim_ws=args.work, verbosity_level=0)
flopy.mf6.ModflowTdis(sim, nper=1, perioddata=[(1.0, 1, 1.0)])
flopy.mf6.ModflowIms(sim, complexity=args.complexity, outer_maximum=args.outer, inner_maximum=200,
                     outer_dvclose=0.01, inner_dvclose=0.001, linear_acceleration="BICGSTAB")
gwf = flopy.mf6.ModflowGwf(sim, modelname="land", newtonoptions="UNDER_RELAXATION", save_flows=False)
flopy.mf6.ModflowGwfdisu(gwf, nodes=n, nja=nja, top=bottom + size, bot=bottom, area=np.full(n, size * size),
                         iac=iac, ja=ja, ihc=ihc, cl12=cl12, hwva=hwva)
flopy.mf6.ModflowGwfic(gwf, strt=bottom + 0.05 * size)
flopy.mf6.ModflowGwfnpf(gwf, icelltype=1, k=K, k33=K)
# Steady state: no storage package.
landing = np.nonzero(rain > 0)[0]
flopy.mf6.ModflowGwfrch(gwf, stress_period_data={0: [((int(i),), float(rain[i] * size)) for i in landing]})
drains = np.nonzero(voids > 0)[0]
flopy.mf6.ModflowGwfdrn(gwf, stress_period_data={0: [((int(i),), float(bottom[i]), float(voids[i] * k[i] * size * size))
                                                     for i in drains]})
flopy.mf6.ModflowGwfoc(gwf, head_filerecord="land.hds", saverecord=[("HEAD", "LAST")])
t = time.time()
sim.write_simulation(silent=True)
print(f"wrote the model in {time.time() - t:.1f} s")
t = time.time()
ok, out = sim.run_simulation(silent=True, report=True)
print(f"MODFLOW {'converged' if ok else 'FAILED'} in {time.time() - t:.1f} s")
for line in out:
    if "OUTER" in line.upper() and "ITERATION" in line.upper() or "converge" in line.lower() or "TOTAL" in line:
        print("  " + line.strip())

h = gwf.output.head().get_data().ravel()
h = np.where(h < -1e29, bottom, h)
np.save(os.path.join(args.work, "heads.npy"), h)
open_blocks = np.maximum(0, np.minimum(h, bottom + size) - (bottom + size * solid))
cols = np.bincount(column, open_blocks, head["nx"] * head["nz"])
land = np.bincount(column, np.ones(n), head["nx"] * head["nz"]) > 0
d = cols[land]
print("open water over land columns: " + ", ".join(
    f"{name} {100 * np.mean(sel):.0f}%" for name, sel in
    [("none", d <= 0.01), ("under 1/4", (d > 0.01) & (d < 0.25)), ("to 1", (d >= 0.25) & (d < 1)),
     ("1-4", (d >= 1) & (d < 4)), ("4-16", (d >= 4) & (d < 16)), ("deeper", d >= 16)]))
