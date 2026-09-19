// Grid-mesh fill benchmark, three strategies.
//   managed    : float[]/int[] + Parallel.For + Vector<float>, scalar stores
//   nt-parallel: aligned native buffers + Parallel.For + AVX non-temporal stores
//   nt-spinpool: same kernel + persistent spinning thread pool
// Run: dotnet run -c Release

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

unsafe static class Program
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

    // ---------- managed baseline ----------

    static void FillManaged(float[] verts, int[] faces, int n)
    {
        float size = n - 1;
        int W = Vector<float>.Count;
        float[] lanes = new float[W];
        for (int l = 0; l < W; l++) lanes[l] = l;
        var laneIdx = new Vector<float>(lanes);

        Parallel.For(0, n, i =>
        {
            float fi = i;
            float x = fi / size * 20f - 10f;
            float sz = MathF.Sin(fi * Freq) * 4f;
            int vbase = i * n * 3;
            int row = i * n;
            int fbase = i * (n - 1) * 4;

            int j = 0;
            for (; j + W <= n; j += W)
            {
                var fj = new Vector<float>(j) + laneIdx;
                var yv = fj / size * 20f - new Vector<float>(10f);
                var zv = sz * Vector.Cos(fj * Freq);
                int vb = vbase + j * 3;
                for (int l = 0; l < W; l++)
                {
                    int p = vb + l * 3;
                    verts[p] = x;
                    verts[p + 1] = yv[l];
                    verts[p + 2] = zv[l];
                }
            }
            for (; j < n; j++)
            {
                float fj = j;
                int p = vbase + j * 3;
                verts[p] = x;
                verts[p + 1] = fj / size * 20f - 10f;
                verts[p + 2] = sz * MathF.Cos(fj * Freq);
            }

            if (i < n - 1)
            {
                for (int q = 0; q < n - 1; q++)
                {
                    int a = row + q;
                    int f = fbase + q * 4;
                    faces[f] = a;
                    faces[f + 1] = a + 1;
                    faces[f + 2] = a + n + 1;
                    faces[f + 3] = a + n;
                }
            }
        });
    }

    // ---------- non-temporal kernel ----------

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void FillRowNt(float* vp, int* fp, int i, int n)
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

    static void FillNtParallel(float* vp, int* fp, int n)
    {
        Parallel.For(0, n, i => FillRowNt(vp, fp, i, n));
        Sse.StoreFence();
    }

    sealed class SpinPool : IDisposable
    {
        readonly Thread[] _threads;
        readonly int _workers;
        Action<int>? _job;
        long _generation;
        long _done;
        bool _dead;

        public SpinPool()
        {
            _workers = Environment.ProcessorCount;
            _threads = new Thread[_workers];
            for (int w = 0; w < _workers; w++)
            {
                int id = w;
                _threads[w] = new Thread(() => Loop(id)) { IsBackground = true };
                _threads[w].Start();
            }
        }

        void Loop(int id)
        {
            long seen = 0;
            while (true)
            {
                SpinWait sw = new();
                while (Volatile.Read(ref _generation) == seen)
                {
                    if (_dead) return;
                    sw.SpinOnce();
                }
                seen = Volatile.Read(ref _generation);
                _job!(id);
                Interlocked.Increment(ref _done);
            }
        }

        public void Run(Action<int> job)
        {
            _job = job;
            Interlocked.Exchange(ref _done, 0);
            Interlocked.Increment(ref _generation);
            SpinWait sw = new();
            while (Volatile.Read(ref _done) < _workers) sw.SpinOnce();
        }

        public void Dispose()
        {
            _dead = true;
            Interlocked.Increment(ref _generation);
        }
    }

    static void FillNtThreads(float* vp, int* fp, int n, SpinPool pool)
    {
        int workers = Environment.ProcessorCount;
        int chunk = (n + workers - 1) / workers;
        pool.Run(w =>
        {
            int lo = w * chunk;
            int hi = Math.Min(n, lo + chunk);
            for (int i = lo; i < hi; i++) FillRowNt(vp, fp, i, n);
        });
        Sse.StoreFence();
    }

    // ---------- harness ----------

    static void Bench(int n, int iters = 8)
    {
        var vertsM = new float[n * n * 3];
        var facesM = new int[(n - 1) * (n - 1) * 4];
        float* vp = (float*)NativeMemory.AlignedAlloc((nuint)n * (nuint)n * 3 * 4, 32);
        int* fp = (int*)NativeMemory.AlignedAlloc((nuint)(n - 1) * (nuint)(n - 1) * 4 * 4, 32);

        Console.WriteLine($"n={n}  verts={n * n,10:N0}  faces={(n - 1) * (n - 1),10:N0}");

        void RunStrategy(string name, Action run)
        {
            run();
            run();
            var times = new double[iters];
            for (int k = 0; k < iters; k++)
            {
                var sw = Stopwatch.StartNew();
                run();
                times[k] = sw.Elapsed.TotalMilliseconds;
            }
            Console.WriteLine($"  {name}  min={times.Min(),8:F3} ms   avg={times.Average(),8:F3} ms");
        }

        RunStrategy("managed    ", () => FillManaged(vertsM, facesM, n));
        RunStrategy("nt-parallel", () => FillNtParallel(vp, fp, n));
        using (var pool = new SpinPool())
        {
            RunStrategy("nt-spinpool", () => FillNtThreads(vp, fp, n, pool));
        }

        var vn = new Span<float>(vp, n * n * 3);
        var fn = new Span<int>(fp, (n - 1) * (n - 1) * 4);
        int diffs = 0;
        for (int i = 0; i < vn.Length; i++) if (vn[i] != vertsM[i]) diffs++;
        Console.WriteLine($"  [check] nt vs managed: vert float diffs={diffs}, " +
                          $"faces equal={fn.SequenceEqual(facesM)}");

        NativeMemory.AlignedFree(vp);
        NativeMemory.AlignedFree(fp);
    }

    static int Main()
    {
        Console.WriteLine("C# god-tier grid fill  (Vector.Cos + AVX NT stores + manual partitioning)");
        Bench(1024);
        Bench(2048);
        Bench(4096);
        return 0;
    }
}
