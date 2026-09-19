// C# full-power grid fill: Parallel.For + System.Numerics SIMD.
// Identical algorithm to mojo/grid_fill.mojo for apples-to-apples timing.
// Run: dotnet run -c Release

using System.Diagnostics;
using System.Numerics;

static class Program
{
    const float Freq = 0.05f;

    static void FillGrid(float[] verts, int[] faces, int n)
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

    static void Bench(int n, int iters = 8)
    {
        var verts = new float[n * n * 3];
        var faces = new int[(n - 1) * (n - 1) * 4];

        FillGrid(verts, faces, n);
        FillGrid(verts, faces, n); // warmup + JIT

        var times = new double[iters];
        for (int k = 0; k < iters; k++)
        {
            var sw = Stopwatch.StartNew();
            FillGrid(verts, faces, n);
            times[k] = sw.Elapsed.TotalMilliseconds;
        }

        double vsum = 0;
        foreach (var v in verts) vsum += v;
        long fsum = 0;
        foreach (var f in faces) fsum += f;

        Console.WriteLine(
            $"n={n} verts={n * n} min={times.Min():F4}ms avg={times.Average():F4}ms  vsum={vsum} fsum={fsum}");
    }

    static int Main()
    {
        Console.WriteLine("C# FillGrid (Vector<float> + Parallel.For)");
        Bench(1024);
        Bench(2048);
        Bench(4096);
        return 0;
    }
}
