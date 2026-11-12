namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 样稿 sim.js 的 rng(seed)（mulberry32）：同一个种子给出与 JS 逐位相同的 [0, 1) 序列，演示数据（历史噪声、模块时间等）因此与样稿一致。
/// JS 的 Math.imul / &gt;&gt;&gt; / | 0 都按 32 位整数运算，这里用 int / uint 照做。
/// </summary>
public sealed class DemoRng
{
    private int _a;

    public DemoRng(double seed) => _a = unchecked((int)ToUint32(seed));

    /// <summary>下一个 [0, 1) 的数。</summary>
    public double Next()
    {
        unchecked
        {
            _a = _a + 0x6D2B79F5;
            int a = _a;
            int t = (a ^ (int)((uint)a >> 15)) * (1 | a);
            t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
            return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
        }
    }

    /// <summary>JS 的 x &gt;&gt;&gt; 0（ToUint32）。</summary>
    private static uint ToUint32(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return 0;
        double t = System.Math.Truncate(x) % 4294967296.0;
        if (t < 0) t += 4294967296.0;
        return (uint)t;
    }
}
