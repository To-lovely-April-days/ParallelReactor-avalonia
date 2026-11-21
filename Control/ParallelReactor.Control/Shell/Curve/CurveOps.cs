using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 曲线升温的下发（检查器「启动曲线」、曲线窗口「保存」/「保存并启动」共用）。一路的顺序同触屏的要求：
/// 升温方式不是曲线时先切到曲线（PUT …/temperature/mode）→ 整条曲线（PUT …/profile）→ 启动（POST …/profile/start）。
/// 几路一起时逐路做，一路失败不影响其它路；最后合成一条提示。
/// </summary>
public static class CurveOps
{
    /// <summary>本机最近一次给这一路保存的曲线：HMI 在「定值」方式下不上报曲线，窗口再打开时用它（没有就用默认的一条）。</summary>
    private static readonly Dictionary<(Guid, int), IReadOnlyList<CurveSeg>> Drafts = new();

    /// <summary>这一路现在的曲线：设备上报的 → 本机上次保存的 → 默认的一条。</summary>
    public static IReadOnlyList<CurveSeg> SegsFor(DeviceVm d, ReactorVm r)
    {
        if (r.Curve is { Segs.Count: > 0 } c) return c.Segs;
        return Drafts.TryGetValue((d.Source.Id, r.N), out var s) ? s : HeatCurve.Default(d.Model.Limits.TempMaxC);
    }

    /// <summary>不能保存 / 启动的原因（"" = 可以）：按下发的顺序逐项问守卫。</summary>
    public static string Why(DeviceVm d, ReactorVm r, IReadOnlyList<CurveSeg> segs, bool start)
    {
        string w = "";
        if (r.Curve is not { CurveMode: true }) w = d.Actions.Why(DeviceAction.HeatMode, new ActionPayload(N: r.N, Id: "curve"));
        if (w.Length == 0) w = d.Actions.Why(DeviceAction.CurveSet, new ActionPayload(N: r.N, Segs: segs));
        if (w.Length == 0 && start) w = d.Actions.Why(DeviceAction.CurveStart, ActionPayload.Ch(r.N));
        return w;
    }

    /// <summary>把同一条曲线下发到几路（<paramref name="start"/> = 接着启动）。返回成功的路数。</summary>
    public static async Task<int> ApplyAsync(ShellVm shell, DeviceVm d, IReadOnlyList<int> ns, IReadOnlyList<CurveSeg> segs, bool start)
    {
        var ok = new List<int>();
        var bad = new List<string>();
        foreach (int n in ns)
        {
            if (d.Reactor(n) is not { Installed: true } r) continue;
            string? fail = null;
            if (r.Curve is not { CurveMode: true })
                fail = Fail(await d.Actions.RunAsync(DeviceAction.HeatMode, new ActionPayload(N: n, Id: "curve"), quiet: true).ConfigureAwait(true));
            if (fail == null) fail = Fail(await d.Actions.RunAsync(DeviceAction.CurveSet, new ActionPayload(N: n, Segs: segs), quiet: true).ConfigureAwait(true));
            if (fail == null) Drafts[(d.Source.Id, n)] = segs;
            if (fail == null && start) fail = Fail(await d.Actions.RunAsync(DeviceAction.CurveStart, ActionPayload.Ch(n), quiet: true).ConfigureAwait(true));
            if (fail == null) ok.Add(n);
            else bad.Add(Rv(n) + "：" + Strip(fail, d.Alias, n));
        }
        string what = start ? " 曲线升温已启动" : " 曲线已保存";
        if (ok.Count > 0) shell.Toast(d.Alias + " · " + string.Join("、", ok.Select(Rv)) + what + " · 共 " + segs.Count + " 段", ToastKind.Ok);
        if (bad.Count > 0) shell.Toast(d.Alias + " · 没有" + (start ? "启动" : "保存") + " · " + string.Join("；", bad), ToastKind.Warn);
        return ok.Count;
    }

    private static string? Fail(ActionOutcome o) => o.Ok ? null : o.Message;

    private static string Rv(int n) => "RV" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>原因里去掉开头的别名 / RV 号（提示里已经写了）。</summary>
    public static string Strip(string why, string alias, int n)
    {
        string s = why;
        if (s.StartsWith(alias + " · ", StringComparison.Ordinal)) s = s[(alias.Length + 3)..];
        string rv = Rv(n) + " ";
        return s.StartsWith(rv, StringComparison.Ordinal) ? s[rv.Length..] : s;
    }

    /// <summary>设备的「现在」：演示 = 演示时钟；真机 = 设备时钟。</summary>
    public static DateTime NowOf(ShellVm shell, DeviceVm d) => d.Source.IsDemo ? shell.Fleet.Now : d.Source.History.Now;
}
