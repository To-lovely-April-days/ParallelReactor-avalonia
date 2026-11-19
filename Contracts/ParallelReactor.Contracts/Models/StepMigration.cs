using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 转换用的一步（线上层面）：类型码、参数（选项 = 英文码字符串，数值 = double，压力 = psi）、曲线段。
/// HMI（ProgramJson / 草稿）与 PC（程序库 / 草稿 / 记录）都先换成它，转换完再换回自己的模型。
/// </summary>
public sealed class MigStep
{
    public MigStep(string type, IDictionary<string, object>? p = null, IEnumerable<(double Temp, double Rate, double Hold)>? segments = null)
    {
        Type = type;
        P = p != null ? new Dictionary<string, object>(p, StringComparer.Ordinal) : new Dictionary<string, object>(StringComparer.Ordinal);
        Segments = segments?.ToList() ?? new List<(double, double, double)>();
    }

    public string Type { get; }
    public Dictionary<string, object> P { get; }
    public List<(double Temp, double Rate, double Hold)> Segments { get; }

    public double? Num(string k) => P.TryGetValue(k, out var v) ? v switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) => x,
        _ => null,
    } : null;

    public string? Str(string k) => P.TryGetValue(k, out var v) && v is string s ? s : null;

    public MigStep With(string k, object v) { P[k] = v; return this; }
}

/// <summary>
/// 旧格式 → v0.8 步骤（research/fusion-port/notes/STEPS-SPEC.md §3）。纯逻辑，HMI 与 PC 共用，两边不会转得不一样。
/// <list type="bullet">
/// <item>旧格式的识别：类型 purge / wait；stir 带 dur；end 带 mode / cool；react 带 endBy。</item>
/// <item>一个旧步骤可能变成几个新步骤；转换后末尾保证只有一个 end（没有就补，多余的去掉，只留最后）。</item>
/// <item>新格式的程序原样返回（只补末尾的 end）；<see cref="Result.Migrated"/> 告诉调用方有没有转换过。</item>
/// </list>
/// </summary>
public static class StepMigration
{
    public sealed record Result(IReadOnlyList<MigStep> Steps, bool Migrated);

    /// <summary>这一步是不是旧格式。</summary>
    public static bool IsLegacy(MigStep s) => s.Type switch
    {
        "purge" or "wait" => true,
        "stir" => s.P.ContainsKey("dur"),
        "end" => s.P.ContainsKey("mode") || s.P.ContainsKey("cool"),
        "react" => s.P.ContainsKey("endBy"),
        _ => false,
    };

    public static Result Migrate(IReadOnlyList<MigStep> input)
    {
        var o = new List<MigStep>();
        bool migrated = false;
        double? lastHeat = null, lastPress = null;
        foreach (var s in input)
        {
            if (!IsLegacy(s))
            {
                if (s.Type == "heat") lastHeat = HeatTarget(s) ?? lastHeat;
                if (s.Type == "press") lastPress = s.Num("pres") ?? lastPress;
                o.Add(s);
                continue;
            }
            migrated = true;
            switch (s.Type)
            {
                case "purge":
                {
                    var e = new MigStep("exchange");
                    e.With("gas", s.Str("gas") ?? "inert").With("cycles", 0.0).With("lineFlush", s.Num("cycles") ?? 10);
                    o.Add(e);
                    break;
                }
                case "stir":
                {
                    double rpm = s.Num("rpm") ?? 400, dur = s.Num("dur") ?? 0;
                    o.Add(new MigStep("stir").With("rpm", rpm));
                    if (dur > 0)
                    {
                        // 旧搅拌步骤停留 dur 分钟（rpm = 0 时也停留）：保持 dur，转过的再停，行为不变
                        o.Add(new MigStep("hold").With("until", "time").With("time", dur));
                        if (rpm > 0) o.Add(new MigStep("stir").With("rpm", 0.0));
                    }
                    break;
                }
                case "wait":
                {
                    string mode = s.Str("mode") ?? "fixed";
                    double time = s.Num("time") ?? 30;
                    if (mode == "temp")
                        o.Add(new MigStep("hold").With("until", "tempAbove").With("temp", lastHeat ?? 40).With("maxWait", Math.Max(1, time)).With("onTimeout", "continue"));
                    else if (mode == "pressure")
                        o.Add(new MigStep("hold").With("until", "pressAbove").With("pres", lastPress is double lp ? Math.Round(lp * 0.97, 1) : 14.5)
                            .With("maxWait", Math.Max(1, time)).With("onTimeout", "continue"));
                    else
                        o.Add(new MigStep("hold").With("until", "time").With("time", time));
                    break;
                }
                case "react":
                {
                    string endBy = s.Str("endBy") ?? "time";
                    var r = new MigStep("react").With("time", s.Num("time") ?? 120).With("minTime", 0.0)
                        .With("mmolOn", endBy is "gasConsumed" or "either" ? "on" : "off").With("mmol", s.Num("mmol") ?? 5).With("stallOn", "off");
                    o.Add(r);
                    break;
                }
                case "end":
                {
                    string mode = s.Str("mode") ?? "seal";
                    if (s.Num("cool") is double cool)
                        o.Add(new MigStep("hold").With("until", "tempBelow").With("temp", cool).With("maxWait", 60.0).With("onTimeout", "continue"));
                    if (mode is "vent" or "quench") o.Add(new MigStep("vent").With("flush", 0.0));
                    else if (mode == "purge") o.Add(new MigStep("exchange").With("gas", "inert").With("cycles", 0.0).With("lineFlush", 3.0));
                    o.Add(new MigStep("end"));
                    break;
                }
            }
        }
        // 末尾只有一个 end：去掉中间的，没有就补
        int last = o.FindLastIndex(x => x.Type == "end");
        migrated |= o.Count(x => x.Type == "end") > 1;   // 多余的 end 被去掉：程序变了，也要提示检查
        var final = new List<MigStep>(o.Count + 1);
        for (int i = 0; i < o.Count; i++)
            if (o[i].Type != "end" || i == last) final.Add(o[i]);
        if (last < 0 || final[^1].Type != "end")
        {
            // 末尾不是 end（例如 end 后面还有步骤）：挪到最后
            final.RemoveAll(x => x.Type == "end");
            final.Add(new MigStep("end"));
            migrated |= last >= 0;
        }
        return new Result(final, migrated);
    }

    private static double? HeatTarget(MigStep s) =>
        s.Str("mode") == "curve" && s.Segments.Count > 0 ? s.Segments[^1].Temp : s.Num("temp");
}
