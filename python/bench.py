"""Benchmark: Python vs Mojo geometry creation inside Blender.

Run:  blender -b --python bench.py
"""

import os
import sys
import time

import bpy
import numpy as np

sys.path.insert(
    0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "addon", "mojo_cube")
)
import mojo_geo

FREQ = 0.05


def reset():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def bench(name, fn, iters):
    fn()
    reset()
    ts = []
    for _ in range(iters):
        t = time.perf_counter()
        fn()
        ts.append((time.perf_counter() - t) * 1e3)
        reset()
    ts.sort()
    print(f"{name:46s} min {ts[0]:9.3f} ms   med {ts[len(ts)//2]:9.3f} ms")


def link_mesh(name, verts, edges, faces):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, edges, faces)
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    return me


# ---------- Test A: default cube ----------

PY_VERTS = [
    (-1.0, -1.0, -1.0), (-1.0, 1.0, -1.0), (1.0, 1.0, -1.0), (1.0, -1.0, -1.0),
    (-1.0, -1.0, 1.0), (-1.0, 1.0, 1.0), (1.0, 1.0, 1.0), (1.0, -1.0, 1.0),
]
PY_EDGES = [
    (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6),
    (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7),
]
PY_FACES = [
    (0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1),
    (1, 5, 6, 2), (2, 6, 7, 3), (4, 0, 3, 7),
]


def cube_bpy_ops():
    bpy.ops.mesh.primitive_cube_add()


def cube_mojo():
    d = mojo_geo.cube()
    link_mesh("M", d["verts"], d["edges"], d["faces"])


def cube_python():
    link_mesh("M", PY_VERTS, PY_EDGES, PY_FACES)


# ---------- Test B: heavy grid geometry ----------

def gen_mojo(n):
    verts = np.empty((n * n, 3), dtype=np.float32)
    faces = np.empty(((n - 1) * (n - 1), 4), dtype=np.int32)
    mojo_geo.grid_into(verts, faces, n)
    return verts, faces


def gen_numpy(n):
    ii = np.arange(n, dtype=np.float32)
    ax = ii / (n - 1) * 20.0 - 10.0
    xx, yy = np.meshgrid(ax, ax, indexing="ij")
    zz = np.sin(ii * FREQ)[:, None] * np.cos(ii * FREQ)[None, :] * 4.0
    verts = np.stack([xx, yy, zz], axis=-1).reshape(-1, 3)
    idx = np.arange(n * n, dtype=np.int32).reshape(n, n)
    a = idx[:-1, :-1].ravel()
    faces = np.stack([a, a + 1, a + n + 1, a + n], axis=-1)
    return verts, faces


def gen_python(n):
    import math
    size = n - 1
    verts = []
    ap = verts.append
    for i in range(n):
        fi = i / size * 20.0 - 10.0
        sz = math.sin(i * FREQ) * 4.0
        for j in range(n):
            ap((fi, j / size * 20.0 - 10.0, sz * math.cos(j * FREQ)))
    faces = [
        (a, a + 1, a + n + 1, a + n)
        for i in range(n - 1)
        for a in range(i * n, i * n + n - 1)
    ]
    return verts, faces


def gen_mojo_lists(n):
    d = mojo_geo.grid_mesh(n)
    return d["verts"], d["faces"]


def e2e_args(gen):
    verts, faces = gen
    return verts, [], faces


print("=" * 78)
print("TEST A — default cube (8 verts / 6 quads), 200 iterations")
print("=" * 78)
bench("bpy.ops.mesh.primitive_cube_add  (operator)", cube_bpy_ops, 200)
bench("mojo_geo.cube() + from_pydata    (mojo)", cube_mojo, 200)
bench("python literals + from_pydata    (python)", cube_python, 200)

for n in (1024, 2048):
    print()
    print("=" * 78)
    print(f"TEST B — grid {n}x{n} ({n*n:,} verts / {(n-1)**2:,} quads), generation only")
    print("=" * 78)
    bench(f"mojo grid_into (np buffers)", lambda n=n: gen_mojo(n), 3)
    bench(f"numpy vectorized           ", lambda n=n: gen_numpy(n), 3)
    if n <= 1024:
        bench(f"pure python loops          ", lambda n=n: gen_python(n), 2)
        bench(f"mojo grid_mesh (py lists)  ", lambda n=n: gen_mojo_lists(n), 2)

    # correctness check
    vm, fm = gen_mojo(n)
    vn, fn = gen_numpy(n)
    print(f"  [check] mojo vs numpy: verts {np.allclose(vm, vn, atol=2e-4)} "
          f"(max diff {np.abs(vm - vn).max():.2e}), faces {np.array_equal(fm, fn)}")

    print()
    print(f"  end-to-end (gen + mesh.from_pydata + link), n={n}")
    bench("mojo e2e   ", lambda n=n: link_mesh("G", *e2e_args(gen_mojo(n))), 3)
    bench("numpy e2e  ", lambda n=n: link_mesh("G", *e2e_args(gen_numpy(n))), 3)

print()
print("done")
