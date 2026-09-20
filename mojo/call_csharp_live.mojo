"""Mojo owns the C# runtime: kick off a fill on .NET's thread pool and watch
live progress through a shared pointer — C# never calls back, it just writes.

Run: pixi run mojo run mojo/call_csharp_live.mojo
"""

from std.ffi import OwnedDLHandle
from std.memory import Layout, alloc, dealloc
from std.time import perf_counter_ns, sleep


def main() raises:
    var lib = OwnedDLHandle(
        "csharp_native/bin/Release/net10.0/linux-x64/publish/NativeMesh.so"
    )
    var get_progress = lib.get_function[Int64]("get_progress_ptr")
    var fill_async = lib.get_function("fill_grid_async")

    var progress_addr = get_progress()
    var progress = Pointer[Int32, MutAnyOrigin](
        unsafe_from_address=Int(progress_addr)
    )

    var n = 4096
    var v_alloc = alloc(Layout[Float32](count=n * n * 3))
    var f_alloc = alloc(Layout[Int32](count=(n - 1) * (n - 1) * 4))
    var vptr = v_alloc.unsafe_ptr()
    var fptr = f_alloc.unsafe_ptr()

    print("mojo: starting C# fill_grid_async for n =", n)
    var t0 = perf_counter_ns()
    fill_async(vptr, fptr, n)

    var last = -1
    while True:
        var p = Int(progress.unsafe_load())
        var bucket = p * 16 // n
        if bucket != last:
            print("  live progress:", p, "/", n, "rows")
            last = bucket
        if p >= n:
            break
        sleep(0.0005)

    var dt = Float64(perf_counter_ns() - t0) / 1e6
    print("done in", dt, "ms — C# thread pool filled mojo's buffers")
    print(
        "sample vert:", vptr[unsafe_offset=0], vptr[unsafe_offset=1],
        vptr[unsafe_offset=2],
    )
    dealloc(v_alloc^)
    dealloc(f_alloc^)
