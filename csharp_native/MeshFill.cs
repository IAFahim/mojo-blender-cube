// NativeAOT-exported grid fill: same NT-store kernel as the benchmark.
// Export: fill_grid(float* verts, int* faces, long n)

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public static unsafe class MeshFill
{
    const float Freq = 0.05f;

    static readonly Vector256<float> LaneIdx = Vector256.Create(0f, 1f, 2f, 3f, 4f, 5f, 6f, 7f);

    static readonly Vector256<int> IdxY0 = Vector256.Create(0, 0, 0, 0, 1, 0, 0, 2);
    static readonly Vector256<int> IdxZ0 = Vector256.Create(0, 0, 0, 0, 0, 1, 0, 0);
    static readonly Vector256<int> IdxY1 = Vector256.Create(0, 0, 3, 0, 0, 4, 0, 0);
    static readonly Vector256<int> IdxZ1 = Vector256.Create(2, 0, 0, 3, 0, 0, 4, 0);
    static readonly Vector256<int> IdxY2 = Vector256.Create(5, 0, 0, 6, 0, 0, 7, 0);
    static readonly Vector256<int> IdxZ2 = Vector256.Create(0, 5, 0, 0, 6, 0, 0, 7);

    static readonly Vector256<int> FaceSeed = Vector256.Create(0, 0, 0, 0, 1, 1, 1, 1);
    static readonly Vector256<int> FaceStep = Vector256.Create(2);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void FillRowNt(float* vp, int* fp, int i, int n)
    {
        float size = n - 1;
        float fi = i;
        float x = fi / size * 20f - 10f;
        float sz = MathF.Sin(fi * Freq) * 4f;
        float* row = vp + (long)i * n * 3;
        var xs = Vector256.Create(x);

        for (int j = 0; j + 8 <= n; j += 8)
        {
            var fj = Vector256.Create((float)j) + LaneIdx;
            var yv = fj / size * 20f - Vector256.Create(10f);
            var zv = sz * Vector256.Cos(fj * Freq);
            var yi = yv.AsInt32();
            var zi = zv.AsInt32();

            var o0 = Avx.Blend(
                Avx.Blend(xs, Avx2.PermuteVar8x32(yi, IdxY0).AsSingle(), 0x92),
                Avx2.PermuteVar8x32(zi, IdxZ0).AsSingle(), 0x24);
            var o1 = Avx.Blend(
                Avx.Blend(xs, Avx2.PermuteVar8x32(yi, IdxY1).AsSingle(), 0x24),
                Avx2.PermuteVar8x32(zi, IdxZ1).AsSingle(), 0x49);
            var o2 = Avx.Blend(
                Avx.Blend(xs, Avx2.PermuteVar8x32(yi, IdxY2).AsSingle(), 0x49),
                Avx2.PermuteVar8x32(zi, IdxZ2).AsSingle(), 0x92);

            float* dst = row + j * 3;
            Avx.StoreAlignedNonTemporal(dst, o0);
            Avx.StoreAlignedNonTemporal(dst + 8, o1);
            Avx.StoreAlignedNonTemporal(dst + 16, o2);
        }

        if (i < n - 1)
        {
            int* fq = fp + (long)i * (n - 1) * 4;
            int q = 0;
            if (((nuint)fq & 31) != 0)
            {
                int a = i * n;
                fq[0] = a;
                fq[1] = a + 1;
                fq[2] = a + n + 1;
                fq[3] = a + n;
                q = 1;
                fq += 4;
            }

            int aBase = i * n + q;
            var acc = Vector256.Create(aBase, aBase + 1, aBase + n + 1, aBase + n,
                                       aBase, aBase + 1, aBase + n + 1, aBase + n)
                      + FaceSeed;
            for (; q + 1 < n - 1; q += 2)
            {
                Avx.StoreAlignedNonTemporal((float*)fq, acc.AsSingle());
                fq += 8;
                acc += FaceStep;
            }
            for (; q < n - 1; q++)
            {
                int a = i * n + q;
                fq[0] = a;
                fq[1] = a + 1;
                fq[2] = a + n + 1;
                fq[3] = a + n;
                fq += 4;
            }
        }
    }

    static readonly int* _progress = (int*)NativeMemory.AllocZeroed(sizeof(int));
    static int* Progress => _progress;

    [UnmanagedCallersOnly(EntryPoint = "get_progress_ptr")]
    public static IntPtr GetProgressPtr() => (IntPtr)Progress;

    [UnmanagedCallersOnly(EntryPoint = "fill_grid")]
    public static void FillGrid(float* verts, int* faces, long n64)
    {
        int n = (int)n64;
        *Progress = 0;
        Parallel.For(0, n, i =>
        {
            FillRowNt(verts, faces, i, n);
            Interlocked.Increment(ref *Progress);
        });
        Sse.StoreFence();
    }

    // Fire-and-forget: fills on the .NET thread pool; caller polls progress ptr.
    [UnmanagedCallersOnly(EntryPoint = "fill_grid_async")]
    public static void FillGridAsync(IntPtr verts, IntPtr faces, long n64)
    {
        int n = (int)n64;
        *Progress = 0;
        Task.Run(() =>
        {
            unsafe
            {
                float* vp = (float*)verts;
                int* fp = (int*)faces;
                Parallel.For(0, n, i =>
                {
                    FillRowNt(vp, fp, i, n);
                    Interlocked.Increment(ref *Progress);
                });
            }
            Sse.StoreFence();
        });
    }
}

public static unsafe class MeshProbes
{
    [UnmanagedCallersOnly(EntryPoint = "probe_hw")]
    public static long ProbeHw() => Vector256.IsHardwareAccelerated ? 1 : 0;

    [UnmanagedCallersOnly(EntryPoint = "probe_cos")]
    public static float ProbeCos(float x)
        => Vector256.Cos(Vector256.Create(x))[0];

    [UnmanagedCallersOnly(EntryPoint = "fill_grid_single")]
    public static void FillGridSingle(float* verts, int* faces, long n64)
    {
        int n = (int)n64;
        MeshFill.FillRowNt(verts, faces, 0, n);
        Sse.StoreFence();
    }
}
