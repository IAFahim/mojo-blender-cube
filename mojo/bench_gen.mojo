"""Native Mojo benchmark for the grid fill (no Python, no Blender).

Build & run:  pixi run mojo run mojo/bench_gen.mojo -I mojo
"""

from std.memory import Layout, alloc, dealloc
from std.time import perf_counter_ns

from grid_fill import fill_grid


def bench(n: Int, iters: Int = 8) raises:
    var v_alloc = alloc(Layout[Float32](count=n * n * 3))
    var f_alloc = alloc(Layout[Int32](count=(n - 1) * (n - 1) * 4))
    var vptr = v_alloc.unsafe_ptr()
    var fptr = f_alloc.unsafe_ptr()

    # warmup (also initializes the thread pool)
    fill_grid(vptr, fptr, n)
    fill_grid(vptr, fptr, n)

    var times = List[Float64]()
    for _ in range(iters):
        var t0 = perf_counter_ns()
        fill_grid(vptr, fptr, n)
        times.append(Float64(perf_counter_ns() - t0) / 1e6)

    var best = times[0]
    var total = 0.0
    for t in times:
        if t < best:
            best = t
        total += t

    # checksums to verify identical math across languages
    var vsum = Float64(0)
    for i in range(n * n * 3):
        vsum += Float64(vptr[unsafe_offset=i])
    var fsum = Int64(0)
    for i in range((n - 1) * (n - 1) * 4):
        fsum += Int64(fptr[unsafe_offset=i])

    print(
        "n=",
        n,
        " verts=",
        n * n,
        " min=",
        best,
        "ms avg=",
        total / Float64(len(times)),
        "ms  vsum=",
        vsum,
        " fsum=",
        fsum,
    )
    dealloc(v_alloc^)
    dealloc(f_alloc^)


def main() raises:
    print("MOJO fill_grid (SIMD + parallelize)")
    bench(1024)
    bench(2048)
    bench(4096)
