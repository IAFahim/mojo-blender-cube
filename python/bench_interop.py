"""Benchmark: bpy method call overhead — Python-native vs Mojo->Python interop.

Run:  blender -b --python bench_interop.py
"""

import os
import sys
import time

import bpy

sys.path.insert(
    0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "addon", "mojo_cube")
)
import mojo_geo


def reset():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def bench_us(name, fn, iters):
    """Report per-call time in microseconds (min / median)."""
    fn()
    reset()
    ts = []
    for _ in range(iters):
        t = time.perf_counter()
        fn()
        ts.append((time.perf_counter() - t) * 1e6)
        reset()
    ts.sort()
    med = ts[len(ts) // 2]
    print(f"{name:52s} min {ts[0]:9.2f} us   med {med:9.2f} us")
    return med


print("=" * 78)
print("INTEROP TAX — same bpy call, Python-native vs via Mojo extension")
print("=" * 78)

# --- 1. heavyweight operator: bpy.ops.mesh.primitive_cube_add() ---
print("\n[bpy.ops.mesh.primitive_cube_add]  (op itself ~150us)")
py_op = bench_us("  python: bpy.ops...primitive_cube_add()",
                 lambda: bpy.ops.mesh.primitive_cube_add(), 200)
mj_op = bench_us("  mojo:   interop -> primitive_cube_add()",
                 lambda: mojo_geo.bpy_cube_add(), 200)
print(f"  --> interop delta: {mj_op - py_op:+.2f} us/call "
      f"({(mj_op - py_op) / py_op * 100:+.1f}%)")

# --- 2. medium call: mesh.from_pydata on a fresh mesh ---
print("\n[mesh.from_pydata(cube)]")
d = mojo_geo.cube()
V, E, F = d["verts"], d["edges"], d["faces"]


def py_from_pydata():
    me = bpy.data.meshes.new("m")
    me.from_pydata(V, E, F)


def mj_from_pydata():
    me = bpy.data.meshes.new("m")
    mojo_geo.from_pydata_call(me, V, E, F)


py_fp = bench_us("  python: me.from_pydata(...)", py_from_pydata, 1000)
mj_fp = bench_us("  mojo:   interop -> from_pydata(...)", mj_from_pydata, 1000)
print(f"  --> interop delta: {mj_fp - py_fp:+.2f} us/call "
      f"({(mj_fp - py_fp) / py_fp * 100:+.1f}%)")

# --- 3. cheap call in a tight loop: bpy.data.meshes.new x 10000 ---
# Loop runs inside each side, so this measures pure per-call overhead
# without python-loop vs mojo-loop skewing... (mojo loop is native).
print("\n[bpy.data.meshes.new('tmp')]  x10,000 per iter")
N = 10000
t = time.perf_counter()
for _ in range(N):
    bpy.data.meshes.new("tmp")
py_loop = (time.perf_counter() - t) * 1e6 / N
reset()

t = time.perf_counter()
mojo_geo.meshes_new_loop(N)
mj_loop = (time.perf_counter() - t) * 1e6 / N
reset()
print(f"  python loop: {py_loop:9.2f} us/call")
print(f"  mojo loop:   {mj_loop:9.2f} us/call")
print(f"  --> interop delta: {mj_loop - py_loop:+.2f} us/call "
      f"({(mj_loop - py_loop) / py_loop * 100:+.1f}%)")

print("\ndone")
