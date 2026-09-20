# mojo-blender-cube

Can a **Mojo** extension inside Blender beat Python? An experiment.

A Blender addon that creates the default cube on startup — but the geometry
comes from a Mojo-compiled CPython extension (`mojo_geo.so`) instead of
`bpy.ops`. Then we scale up to millions of vertices to see where Mojo's
compiled SIMD + multithreading actually pays off.

- **Mojo** 1.1.0 + **max-core** 26.6 (pixi, stable channel)
- **Blender** 5.2.1 LTS (embedded Python 3.14.7 — extension `.so` must match
  the interpreter's minor version)
- Linux x86-64

## Layout

```
mojo/mojo_geo.mojo          Mojo source: CPython extension via PythonModuleBuilder
mojo/grid_fill.mojo         shared grid fill (SIMD + parallelize), no Python deps
mojo/bench_gen.mojo         native Mojo benchmark (no Python/Blender needed)
addon/mojo_cube/__init__.py Blender addon (startup cube via load_factory_startup_post)
python/bench.py             Python vs Mojo geometry benchmarks
python/bench_interop.py     bpy call-overhead: Python-native vs Mojo->Python interop
csharp/Program.cs           C# challenger: Parallel.For + System.Numerics SIMD
pixi.toml                   env (mojo + max-core + python 3.14) & tasks
```

## Build & install

```bash
pixi install
pixi run build        # mojo build --emit shared-lib -> addon/mojo_cube/mojo_geo.so

# install the addon (legacy addons dir)
mkdir -p ~/.config/blender/5.2/scripts/addons
ln -s "$PWD/addon/mojo_cube" ~/.config/blender/5.2/scripts/addons/mojo_cube

# enable + persist
blender -b --python-expr "
import addon_utils, bpy
addon_utils.enable('mojo_cube', default_set=True, persistent=True)
bpy.ops.wm.save_userpref()"
```

Launch Blender normally: a `MojoCube` object appears next to the default `Cube`.

## Benchmarks

```bash
pixi run bench           # python/bench.py inside blender -b
pixi run bench-interop   # python/bench_interop.py
```

### Default cube (8 verts, 200 iters)

| Path | median |
|---|---|
| `bpy.ops.mesh.primitive_cube_add` | 0.19 ms |
| `mojo_geo.cube()` + `from_pydata` | **0.03 ms** |
| Python literals + `from_pydata` | 0.03 ms |

The 6x is from bypassing the operator machinery, **not** from Mojo — plain
Python literals are just as fast. 8 verts is nothing to compute.

### Grid generation (n×n sine grid, SIMD + all cores vs NumPy vs loops)

| | n=1024 (1.05M verts) | n=2048 (4.19M verts) |
|---|---|---|
| **Mojo `grid_into` → np buffers** | **0.70 ms** | **3.6 ms** |
| NumPy vectorized | 7.1 ms | 39.6 ms |
| Pure Python loops | 246 ms | — |
| Mojo returning Py lists | 626 ms | — |

**Mojo ≈ 10–11x NumPy, ~350x pure Python.** But note the trap: Mojo
*returning* Python lists (`grid_mesh`) is slower than pure Python — the
winning pattern is `grid_into`, where Python preallocates numpy buffers and
Mojo fills them through raw pointers (`Pointer(unsafe_from_address=...)`
over `arr.ctypes.data`), zero marshalling.

Correctness: max vertex diff vs NumPy 9.5e-7 (float32 trig), faces identical.

### End-to-end (gen + `mesh.from_pydata` + link)

| | n=1024 | n=2048 |
|---|---|---|
| mojo e2e | ~1.27 s | ~5.2 s |
| numpy e2e | ~1.26 s | ~5.2 s |

`from_pydata` ingestion dominates (~1.3 s per 1M verts) — Blender's API
boundary swallows the generation win for one-shot creation. Mojo's 10x pays
off where generation is the *repeated* cost (e.g. `foreach_set` deformation
of an existing mesh).

### Interop tax — Mojo calling bpy vs Python calling bpy

Same bpy call through `PythonObject` interop vs native Python:

| Call | Python | via Mojo | delta |
|---|---|---|---|
| `bpy.ops...primitive_cube_add` (~185µs op) | 184.6 µs | 190.9 µs | +6.3 µs (+3.4%) |
| `mesh.from_pydata` (~24µs) | 24.4 µs | 24.9 µs | +0.5 µs (+2.1%) |
| `bpy.data.meshes.new` ×10k | 12.49 µs | 12.41 µs | **−0.09 µs (−0.7%)** |

Mojo→CPython calls go through the C API directly — no bytecode dispatch —
so they're essentially free, sometimes faster than Python itself.
**The cost boundary is data marshalling, not calls.**

### Language vs language: Mojo vs C# (no Blender)

Same fill algorithm, i9-14900K (32 threads). Three C# strategies vs Mojo
`parallelize` + SIMD:

| | Mojo | C# managed (`Vector`+`Parallel.For`) | C# god-tier (NT stores) |
|---|---|---|---|
| n=1024 (1M verts) | **0.64 ms** | 1.58 ms | 0.97 ms |
| n=2048 (4.2M verts) | 4.25 ms | 4.88 ms | **3.15 ms** |
| n=4096 (16.8M verts) | 22.1 ms | 19.5 ms | **11.6 ms** |

**Mojo wins compute-bound sizes** (cheaper `parallelize` dispatch).
**C# wins memory-bound sizes** — god-tier C# uses non-temporal stores
(`Avx.StoreAlignedNonTemporal`, MOVNTPS) to eliminate read-for-ownership
traffic (~470MB of dead reads at n=4096 → ~1.9x bandwidth). Getting there
required `PermuteVar8x32`+`Blend` to transpose xyz-interleaved data into
contiguous 256-bit stores, `NativeMemory.AlignedAlloc`, and a persistent
spinning thread pool (`SpinPool`). Mojo 1.1 exposes no non-temporal store
API (`llvm_intrinsic` has no movnt intrinsics in its bundled LLVM, no
inline asm) — a current API gap, not a fundamental one.

C# NT output is bitwise identical to the managed baseline.

Run: `pixi run bench-mojo` and `pixi run bench-csharp`.

## Notes

- `parallelize` lives in `max.algorithm` since Mojo 1.0 — requires
  `max-core`, not just the `mojo` package.
- NuMojo v0.10 does **not** support Mojo 1.1 yet (pins `mojo <1.1.0`;
  fails to compile under max-core 26.6).
- The addon `.so` is ABI-bound to CPython 3.14 — rebuild per Blender's
  embedded Python version.

### Mojo calling C# — NativeAOT + dlopen

C# compiled to a native `.so` (`dotnet publish -c Release -r linux-x64`
with `PublishAot`), `fill_grid` exported via `[UnmanagedCallersOnly]`,
called from Mojo through `std.ffi.OwnedDLHandle` — raw pointers, zero
marshalling. `mojo/call_csharp.mojo`:

| | Mojo native fill | C# NT kernel called from Mojo |
|---|---|---|
| 1M verts | **0.64 ms** | 0.79 ms |
| 4.2M verts | 4.25 ms | **3.15 ms** |
| 16.8M verts | 22.1 ms | **11.4 ms** |

Run: `pixi run build-csharp-native` once, then `pixi run bench-mojo-csharp`.

Best architecture: Mojo orchestrates, C# supplies the non-temporal stores
Mojo 1.1 can't emit. Gotcha: NativeAOT defaults to x86-64 baseline ISA —
AVX intrinsics throw `PlatformNotSupported` without
`<IlcInstructionSet>avx2</IlcInstructionSet>`.

Other routes: `hostfxr` embedding (non-AOT managed hosting), or
Mojo→Python→pythonnet (two interop hops).
