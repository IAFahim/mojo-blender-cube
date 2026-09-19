"""Shared grid-mesh fill: n x n vertices + quad faces, SIMD + multithreaded.

Pure Mojo — no Python imports, so both the Blender extension (mojo_geo) and
the native benchmark (bench_gen) can use it.
"""

from max.algorithm import parallelize
from std.math import cos, sin


def fill_grid[
    vo: MutOrigin, fo: MutOrigin
](
    vptr: Pointer[Float32, origin=vo],
    fptr: Pointer[Int32, origin=fo],
    n: Int,
):
    """Fill interleaved xyz verts (n*n*3 f32) and quad faces ((n-1)^2*4 i32)."""
    var size = Float32(n - 1)
    var freq = Float32(0.05)

    comptime W = 8
    var lane_idx = SIMD[.float32, W](0, 1, 2, 3, 4, 5, 6, 7)

    def fill_row(i: Int) {imm vptr, imm fptr, imm n, imm size, imm freq, imm lane_idx}:
        var fi = Float32(i)
        var x = fi / size * 20.0 - 10.0
        var sz = sin(fi * freq) * 4.0
        var vbase = i * n * 3
        var row = i * n
        var fbase = i * (n - 1) * 4

        var j = 0
        while j + W <= n:
            var fj = SIMD[.float32, W](Float32(j)) + lane_idx
            var yv = fj / size * 20.0 - 10.0
            var zv = sz * cos(fj * freq)
            comptime for l in range(W):
                var p = vptr.unsafe_offset(vbase + (j + l) * 3)
                p.unsafe_store(x)
                p.unsafe_offset(1).unsafe_store(yv[l])
                p.unsafe_offset(2).unsafe_store(zv[l])
            j += W
        while j < n:
            var fj = Float32(j)
            var p = vptr.unsafe_offset(vbase + j * 3)
            p.unsafe_store(x)
            p.unsafe_offset(1).unsafe_store(fj / size * 20.0 - 10.0)
            p.unsafe_offset(2).unsafe_store(sz * cos(fj * freq))
            j += 1

        if i < n - 1:
            for q in range(n - 1):
                var a = Int32(row + q)
                var f = fptr.unsafe_offset(fbase + q * 4)
                f.unsafe_store(a)
                f.unsafe_offset(1).unsafe_store(a + 1)
                f.unsafe_offset(2).unsafe_store(a + Int32(n) + 1)
                f.unsafe_offset(3).unsafe_store(a + Int32(n))

    parallelize(fill_row, n)
