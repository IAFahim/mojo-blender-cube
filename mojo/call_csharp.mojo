"""Mojo -> C# (NativeAOT): dlopen NativeMesh.so and call fill_grid.

Run: pixi run mojo run mojo/call_csharp.mojo
"""

from std.ffi import OwnedDLHandle
from std.memory import Layout, alloc, dealloc
from std.time import perf_counter_ns


def main() raises:
    var lib = OwnedDLHandle(
        "csharp_native/bin/Release/net10.0/linux-x64/publish/NativeMesh.so"
    )
    var fill = lib.get_function("fill_grid")
    print("MOJO calling C# NativeAOT fill_grid")

    for n in [1024, 2048, 4096]:
        var v_alloc = alloc(Layout[Float32](count=n * n * 3))
        var f_alloc = alloc(Layout[Int32](count=(n - 1) * (n - 1) * 4))
        var vptr = v_alloc.unsafe_ptr()
        var fptr = f_alloc.unsafe_ptr()

        fill(vptr, fptr, n)
        fill(vptr, fptr, n)

        var times = List[Float64]()
        for _ in range(8):
            var t0 = perf_counter_ns()
            fill(vptr, fptr, n)
            times.append(Float64(perf_counter_ns() - t0) / 1e6)

        var best = times[0]
        var total = 0.0
        for t in times:
            if t < best:
                best = t
            total += t

        var vsum = Float64(0)
        for i in range(n * n * 3):
            vsum += Float64(vptr[unsafe_offset=i])
        var fsum = Int64(0)
        for i in range((n - 1) * (n - 1) * 4):
            fsum += Int64(fptr[unsafe_offset=i])

        print(
            "n=", n, " min=", best, "ms avg=", total / 8.0,
            "ms  vsum=", vsum, " fsum=", fsum,
        )
        dealloc(v_alloc^)
        dealloc(f_alloc^)
