using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Measure;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.PerfTest.Report;

/// <summary>
/// 自包含中文 HTML 报告，样式令牌与 PC 控制软件（research/pc-control.html）一致：--purple #765cc5、背景 #ececee、面板 #f7f7f8、
/// 1 px #c9c9cd 线、Segoe UI/Arial、8–11 px 小字。结构：头部 meta → 前置检查表 → 项目汇总（结论 / 参考值 vs 实测，无合格判定除非方案给硬限）
/// → 每项一节（步骤时间线、基线数据表、§4 记录表、人工录入、操作员备注、SVG 曲线）→ 事件摘要 → 附录（原始文件、公式）。
/// </summary>
public static class HtmlReportWriter
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string Write(RunResult r, TestPlan? plan, SampleRecorder? samples)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(Html.Esc($"性能测试报告 {r.RunId}")).Append("</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");
        sb.Append("<div class=\"page\">");

        if (r.Simulated) sb.Append("<div class=\"banner-sim\">模拟运行（PR_SIM），数据无效，仅用于验证流程</div>");
        if (r.Status == RunStatus.Aborted) sb.Append("<div class=\"banner-abort\">本次运行已中止：").Append(Html.Esc(r.AbortReason ?? "—")).Append("</div>");

        // ① 头部
        sb.Append("<div class=\"panel\"><div class=\"panel-heading\"><strong>RV1–RV4 摸底性能测试报告</strong><span class=\"muted\">").Append(Html.Esc(r.PlanTitle)).Append(" · 版本 ").Append(Html.Esc(r.PlanVersion)).Append("</span></div>");
        sb.Append("<div class=\"meta-grid\">");
        Meta(sb, "报告编号", r.RunId);
        Meta(sb, "日期", r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Ci) + (r.EndedAt.HasValue ? " – " + r.EndedAt.Value.ToLocalTime().ToString("HH:mm:ss", Ci) : ""));
        Meta(sb, "总用时", Dur(r.Elapsed));
        Meta(sb, "操作者", r.Operator);
        Meta(sb, "设备", $"{r.DeviceModel ?? "—"} · 序列号 {r.DeviceSerial ?? "—"}");
        Meta(sb, "触屏软件", $"HMI {r.HmiVersion ?? "—"} · API {r.ApiVersion ?? "—"} · bootId {Short(r.BootId)}");
        Meta(sb, "PC 软件版本", r.SoftwareVersion);
        Meta(sb, "会话", r.SessionId != null ? $"{r.SessionId}（testId {r.SessionTestId}）" : "未建会话");
        Meta(sb, "通道", string.Join(" ", r.Channels.Select(c => "RV" + c)));
        Meta(sb, "测试项", $"{r.Items.Count} 项：{string.Join("、", r.Items.Select(i => i.ItemId))}");
        Meta(sb, "结果状态", StatusZh(r.Status));
        Meta(sb, "时钟同步", r.ClockSynced switch { true => "已同步", false => "未同步（时间以服务端 ts 为准，报告标注）", _ => "未知（以服务端 ts 为准）" });
        sb.Append("</div>");
        var e = r.Environment;
        sb.Append("<div class=\"sub\">环境记录（§0.5）：")
          .Append(Html.Esc($"环境温度 {Opt(e.AmbientTempC, "℃")} · 湿度 {Opt(e.HumidityPct, "%")} · 大气压 {Opt(e.AtmPressureKpa, "kPa")} · 装配编号 {e.AssemblyNo ?? "—"} · 装液 {e.MediumAndMassG ?? "—"} · 电源电压 {Opt(e.SupplyVoltageV, "V")}"))
          .Append(string.IsNullOrEmpty(e.Note) ? "" : " · " + Html.Esc(e.Note)).Append("</div>");
        if (r.SettingsSnapshot is { } ss) sb.Append("<div class=\"sub\">参数快照（开场 GET /settings，§0.5 参数冻结）：").Append(Html.Esc(SettingsSummary(ss))).Append("</div>");
        sb.Append("</div>");

        // ② 前置检查表
        var checklists = r.Items.SelectMany(i => i.Steps.Where(s => s.Kind == nameof(StepKind.Checklist)).Select(s => (Item: i, Step: s))).ToList();
        if (checklists.Count > 0)
        {
            sb.Append("<div class=\"panel\"><div class=\"panel-heading\"><strong>前置检查表</strong><span class=\"muted\">§0.4 · 两人签字</span></div>");
            foreach (var (item, st) in checklists)
            {
                var def = plan?.FindItem(item.ItemId)?.Steps.FirstOrDefault(x => x.Id == st.StepId);
                sb.Append("<table class=\"report-table\"><thead><tr><th>序号</th><th>项目</th><th>结果</th><th>检查人</th><th>复核人</th><th>时间</th></tr></thead><tbody>");
                var items = def?.ChecklistItems ?? new List<string>();
                for (int k = 0; k < Math.Max(items.Count, st.Checked?.Count ?? 0); k++)
                {
                    bool? ok = st.Checked != null && k < st.Checked.Count ? st.Checked[k] : null;
                    sb.Append("<tr><td>").Append(k + 1).Append("</td><td>").Append(Html.Esc(k < items.Count ? items[k] : "—")).Append("</td><td class=\"")
                      .Append(ok == true ? "pass" : ok == false ? "fail" : "").Append("\">").Append(ok switch { true => "✓", false => "✗ 未完成", _ => "—" }).Append("</td><td>")
                      .Append(Html.Esc(st.Signer1 ?? "—")).Append("</td><td>").Append(Html.Esc(st.Signer2 ?? "—")).Append("</td><td>").Append(Html.Esc(Local(st.EndedAt))).Append("</td></tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</div>");
        }

        // ③ 汇总表
        sb.Append("<div class=\"panel\"><div class=\"panel-heading\"><strong>测试项汇总</strong><span class=\"muted\">摸底测试 · 不做合格判定（方案 §3）；仅方案给出硬性判据的项填判定</span></div>");
        sb.Append("<table class=\"report-table\"><thead><tr><th>测试项</th><th>通道</th><th>出处</th><th>开始 – 结束</th><th>结论</th><th>关键实测</th><th>对标参考值（§5）</th><th>判定</th></tr></thead><tbody>");
        foreach (var it in r.Items)
        {
            var def = plan?.FindItem(it.ItemId);
            var key = it.Measures.Take(3).Select(m => $"{m.Label}{(m.Channel.HasValue ? " RV" + m.Channel : "")} = {Metrics.Fmt(m.Value)} {m.Unit}").ToList();
            var refs = def?.ReferenceValues.Select(x => x.Text).Take(2).ToList() ?? new List<string>();
            sb.Append("<tr><td><strong>").Append(Html.Esc(it.ItemId)).Append("</strong> ").Append(Html.Esc(it.Title)).Append("</td><td>")
              .Append(Html.Esc(string.Join(" ", it.Channels.Select(c => "RV" + c)))).Append("</td><td>").Append(Html.Esc(it.SourceRef)).Append("</td><td>")
              .Append(Html.Esc(Local(it.StartedAt) + " – " + Local(it.EndedAt, true))).Append("</td><td class=\"").Append(OutcomeClass(it.Outcome)).Append("\">").Append(OutcomeZh(it.Outcome))
              .Append(string.IsNullOrEmpty(it.Note) ? "" : "<br><span class=\"muted\">" + Html.Esc(it.Note) + "</span>").Append("</td><td>")
              .Append(key.Count == 0 ? "—" : Html.Esc(string.Join("；", key))).Append("</td><td>").Append(refs.Count == 0 ? "—" : Html.Esc(string.Join("；", refs))).Append("</td><td class=\"")
              .Append(it.Verdict == Verdict.Pass ? "pass" : it.Verdict == Verdict.Fail ? "fail" : "").Append("\">").Append(VerdictZh(it)).Append("</td></tr>");
        }
        sb.Append("</tbody></table></div>");

        // ④ 每项
        foreach (var it in r.Items) WriteItem(sb, r, it, plan?.FindItem(it.ItemId), samples);

        // ⑤ 事件摘要
        sb.Append("<div class=\"panel\"><div class=\"panel-heading\"><strong>事件 / 报警摘要</strong><span class=\"muted\">").Append(r.Events.Count).Append(" 条 · 全文见 events.csv</span></div>");
        sb.Append("<table class=\"report-table\"><thead><tr><th>时间</th><th>类别</th><th>测试项</th><th>步骤</th><th>内容</th></tr></thead><tbody>");
        foreach (var ev in r.Events.Where(x => x.Kind is "safety" or "abort" or "alarm" or "command-failed" or "timeout" or "run" or "item" or "step-failed" or "compare" or "entry" or "comm").Take(300))
            sb.Append("<tr><td>").Append(Html.Esc(Local(ev.At))).Append("</td><td class=\"").Append(ev.Kind is "safety" or "abort" or "alarm" or "command-failed" or "step-failed" or "entry" ? "fail" : "").Append("\">").Append(Html.Esc(ev.Kind)).Append("</td><td>")
              .Append(Html.Esc(ev.ItemId)).Append("</td><td>").Append(Html.Esc(ev.StepId)).Append("</td><td>").Append(Html.Esc(ev.Text)).Append("</td></tr>");
        sb.Append("</tbody></table></div>");

        // ⑥ 附录
        sb.Append("<div class=\"panel\"><div class=\"panel-heading\"><strong>附录</strong><span class=\"muted\">原始数据 · 公式 · 不确定度</span></div><div class=\"sub\">");
        sb.Append("<p><strong>原始文件</strong>（与本报告同目录）：").Append(Html.Esc(string.Join(" · ", r.RawFiles.Count > 0 ? r.RawFiles : new List<string> { "raw/telemetry.csv", "events.csv", "manual-entries.csv", "summary.csv" })))
          .Append("。telemetry.csv 为长表：每帧每通道一行（ts,seq,rv,state,tempC,tempSpC,heatOutputPct,pressurePsi,pressureRaw,valveOpen,stirOn,rpmSp,busVoltageV,estop,programState,stepIndex,itemId,stepId）。触屏侧会话全量 CSV（hmi/）为权威原始数据，PC 侧 WS 捕获为副本。</p>");
        sb.Append("<p><strong>公式</strong>（方案 §2）：超调 σ = T_peak − T_s；到温时间 t_r = 首次达到 T_s − 0.5 ℃；稳定时间 t_s(b) = 进入 ±b 带内且之后 ≥ 10 min 不越出的时刻；升温速率 = [T₀+0.1·span, T₀+0.9·span] 区间最小二乘斜率（span = (T_s − Δ) − T₀）；波动度 ΔT_f = ±(T_max − T_min)/2；稳态均值偏差 ΔT_mean = mean(T) − T_s；示值–液温差 ΔT_d = mean(PV) − mean(T_ref)；通道间一致性（静态）= 4 路均值极差；压力衰减 = 测量窗口内最小二乘斜率（psi/h，另给 kPa/h）；转速误差 e_n = (n̄_meas − n_set)/n_set × 100%。曲线与 CSV 中的「转速」为设定值（驱动器无转速反馈），实测转速只来自转速表录入。</p>");
        sb.Append("<p><strong>基线 / 规格草案</strong>（方案 §3，仅作参考）：独立样本数 n 取轮次数（同日 4 通道不独立）；n &lt; 6 只报 x̄ / s / 最差；n ≥ 6 用 ISO 16269-6 容差区间（95% 含量 / 95% 置信）的 k 因子：越小越好取 max(x̄+k₁s, 最差×1.2) 向上取档，越大越好取 min(x̄−k₁s, 最差×0.8) 向下取档（单侧且全部 &gt; 0 的指标先取对数），双侧取 ±max(|x̄|+k₂s, max|x|×1.2)。</p>");
        sb.Append("<p><strong>不确定度备注</strong>：基于 PV 的温度量分辨率 0.1 ℃（q/√12 ≈ 0.03 ℃），1.2 s 采样可能漏掉过冲峰值；人工读数以仪器证书不确定度为准；时间以服务端 ts（UTC）为准，PC 时钟仅作显示")
          .Append(r.ClockSynced == false ? "；<span class=\"fail\">服务端时钟未同步，绝对时刻仅供参考</span>" : "").Append("。</p>");
        sb.Append("</div></div>");

        sb.Append("<div class=\"footer\">生成于 ").Append(Html.Esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Ci))).Append(" · ParallelReactor PC 控制软件 · 性能测试模块</div>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static void WriteItem(StringBuilder sb, RunResult r, ItemResult it, TestItem? def, SampleRecorder? samples)
    {
        sb.Append("<div class=\"panel item\" id=\"item-").Append(Html.Esc(it.ItemId)).Append("\"><div class=\"panel-heading\"><strong>").Append(Html.Esc(it.ItemId)).Append(" ").Append(Html.Esc(it.Title))
          .Append("</strong><span class=\"muted\">").Append(Html.Esc(it.SourceRef)).Append(" · ").Append(Html.Esc(string.Join(" ", it.Channels.Select(c => "RV" + c)))).Append(" · ").Append(OutcomeZh(it.Outcome)).Append("</span></div>");
        if (def != null)
        {
            sb.Append("<div class=\"sub\">").Append(Html.Esc(def.Purpose));
            if (def.Preconditions.Count > 0) sb.Append(" · 前置：").Append(Html.Esc(string.Join("、", def.Preconditions)));
            if (!string.IsNullOrEmpty(def.Instrument)) sb.Append(" · 仪器：").Append(Html.Esc(def.Instrument));
            if (!string.IsNullOrEmpty(def.HardCriterion)) sb.Append(" · <strong>硬性判据：</strong>").Append(Html.Esc(def.HardCriterion));
            if (def.Repeat is { } rp) sb.Append(" · <strong>本次编码重复：</strong>").Append(rp.Iterations).Append(" 轮").Append(rp.Values != null ? "（" + Html.Esc(string.Join("/", rp.Values.Select(v => Metrics.Fmt(v)))) + " " + Html.Esc(rp.Unit ?? "") + "）" : "")
                                                .Append("；方案要求的 n 见「目的」，不足部分由多次运行离线合并");
            sb.Append("</div>");
        }
        if (it.CommStart != null || it.CommEnd != null)
            sb.Append("<div class=\"sub\"><strong>通讯计数（GET /device/comm）</strong>：开始 ").Append(Html.Esc(it.CommStart ?? "—")).Append("<br>结束 ").Append(Html.Esc(it.CommEnd ?? "—")).Append("</div>");

        // 步骤时间线
        sb.Append("<h4>步骤时间线</h4><table class=\"report-table\"><thead><tr><th>步骤</th><th>种类</th><th>轮次</th><th>开始</th><th>结束</th><th>耗时</th><th>结果</th><th>命令 / opId</th><th>备注</th></tr></thead><tbody>");
        foreach (var s in it.Steps)
        {
            var cmds = s.Commands.Count == 0 ? "—" : string.Join("；", s.Commands.Select(c => $"{c.Op}{(c.Channel.HasValue ? " RV" + c.Channel : "")} → {(c.Ok ? c.HttpStatus + " " + c.Status : c.HttpStatus + " " + c.Code)}{(c.OpId != null ? " " + Short(c.OpId) : "")}"));
            sb.Append("<tr><td>").Append(Html.Esc(s.StepId)).Append(" ").Append(Html.Esc(s.Title)).Append("</td><td>").Append(Html.Esc(KindZh(s.Kind))).Append("</td><td>").Append(s.Iteration?.ToString() ?? "—").Append("</td><td>")
              .Append(Html.Esc(Local(s.StartedAt, true))).Append("</td><td>").Append(Html.Esc(Local(s.EndedAt, true))).Append("</td><td>").Append(Html.Esc(Dur(TimeSpan.FromSeconds(s.DurationSec)))).Append("</td><td class=\"")
              .Append(s.Status is StepStatus.Failed or StepStatus.Aborted or StepStatus.TimedOut ? "fail" : s.Status == StepStatus.Done ? "pass" : "").Append("\">").Append(StepZh(s.Status));
            if (s.ComparePassed.HasValue) sb.Append("<br><span class=\"muted\">实际 ").Append(Metrics.Fmt(s.CompareActual ?? double.NaN)).Append(" / 期望 ").Append(Html.Esc(s.CompareExpected)).Append("</span>");
            sb.Append("</td><td>").Append(Html.Esc(cmds)).Append("</td><td>").Append(Html.Esc(s.Note)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        // 基线数据表（§3 格式）
        if (it.Measures.Count > 0)
        {
            sb.Append("<h4>基线数据表（§3）</h4><table class=\"report-table\"><thead><tr><th>指标</th><th>工况</th><th>数据量</th><th>x̄</th><th>s</th><th>实测最差</th><th>扩展不确定度 U</th><th>对标值（§5）</th><th>规格草案（参考）</th><th>判定</th><th>备注</th></tr></thead><tbody>");
            foreach (var g in it.Measures.GroupBy(m => m.Key))
            {
                var first = g.First();
                var vals = g.Select(m => m.Value).Where(v => !double.IsNaN(v)).ToList();
                // §3-2：同日 4 通道不是独立样本 → 独立 n = 轮次数（Repeat）；n < 6 只报 x̄ / s / 最差
                var bl = Metrics.DeriveBaseline(vals, first.Direction, g.Select(m => m.Iteration ?? 1).Distinct().Count());
                var cond = string.Join("、", g.Select(m => m.IterationValue).Where(v => v.HasValue).Select(v => Metrics.Fmt(v!.Value)).Distinct().Take(6));
                sb.Append("<tr><td>").Append(Html.Esc(first.Label)).Append(" <span class=\"muted\">(").Append(Html.Esc(first.Unit)).Append(")</span></td><td>").Append(Html.Esc(cond.Length == 0 ? "—" : cond)).Append("</td><td>")
                  .Append(g.Select(m => m.Channel).Distinct().Count()).Append(" 通道 × ").Append(g.Select(m => m.Iteration ?? 1).Distinct().Count()).Append(" 次</td><td>").Append(Metrics.Fmt(bl.Mean)).Append("</td><td>")
                  .Append(bl.N > 1 ? Metrics.Fmt(bl.Sigma) : "—").Append("</td><td>").Append(Metrics.Fmt(bl.Worst)).Append("</td><td>—（见附录）</td><td>").Append(Html.Esc(first.Reference5 ?? "—")).Append("</td><td>")
                  .Append(Html.Esc(bl.SpecText)).Append("</td><td>").Append(it.Verdict == Verdict.NotJudged ? "—（摸底）" : VerdictZh(it)).Append("</td><td>").Append(Html.Esc(first.Note)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");

            // 跨通道并排表（§3：RV1–RV4 并排）
            var perCh = it.Measures.Where(m => m.Channel.HasValue).GroupBy(m => m.Key).ToList();
            if (perCh.Count > 0)
            {
                var chans = it.Channels;
                sb.Append("<h4>通道并排（实测值）</h4><table class=\"report-table\"><thead><tr><th>指标</th><th>轮次</th>");
                foreach (var c in chans) sb.Append("<th>RV").Append(c).Append("</th>");
                sb.Append("<th>极差</th></tr></thead><tbody>");
                foreach (var g in perCh)
                    foreach (var ig in g.GroupBy(m => m.Iteration ?? 1).OrderBy(x => x.Key))
                    {
                        var row = chans.Select(c => ig.FirstOrDefault(m => m.Channel == c)?.Value ?? double.NaN).ToList();
                        var f = g.First();
                        sb.Append("<tr><td>").Append(Html.Esc(f.Label)).Append(" <span class=\"muted\">(").Append(Html.Esc(f.Unit)).Append(")</span></td><td>").Append(ig.Key)
                          .Append(ig.First().IterationValue.HasValue ? " <span class=\"muted\">" + Metrics.Fmt(ig.First().IterationValue!.Value) + "</span>" : "").Append("</td>");
                        foreach (var v in row) sb.Append("<td>").Append(Metrics.Fmt(v)).Append("</td>");
                        sb.Append("<td>").Append(Metrics.Fmt(Metrics.Range(row))).Append("</td></tr>");
                    }
                sb.Append("</tbody></table>");
            }
        }

        // §4 记录表
        if (def is { RecordFields.Count: > 0 })
        {
            sb.Append("<h4>记录表（§4）</h4><table class=\"report-table\"><thead><tr>");
            foreach (var f in def.RecordFields) sb.Append("<th>").Append(Html.Esc(f.Label)).Append(string.IsNullOrEmpty(f.Unit) ? "" : " (" + Html.Esc(f.Unit) + ")").Append("</th>");
            sb.Append("</tr></thead><tbody>");
            var iterations = it.Measures.Select(m => m.Iteration ?? 1).Concat(it.ManualEntries.Select(m => m.Iteration ?? 1)).Distinct().DefaultIfEmpty(1).OrderBy(x => x).ToList();
            foreach (var iter in iterations)
                foreach (var ch in it.Channels.Count == 0 ? new List<int> { 0 } : it.Channels)
                {
                    sb.Append("<tr>");
                    foreach (var f in def.RecordFields) sb.Append("<td>").Append(Html.Esc(RecordValue(r, it, f, iter, ch))).Append("</td>");
                    sb.Append("</tr>");
                }
            sb.Append("</tbody></table>");
        }

        // 人工录入
        if (it.ManualEntries.Count > 0)
        {
            sb.Append("<h4>人工录入</h4><table class=\"report-table\"><thead><tr><th>时间</th><th>步骤</th><th>轮次</th><th>项目</th><th>仪器</th><th>通道</th><th>#</th><th>读数</th><th>单位</th></tr></thead><tbody>");
            foreach (var m in it.ManualEntries)
                sb.Append("<tr><td>").Append(Html.Esc(Local(m.At, true))).Append("</td><td>").Append(Html.Esc(m.StepId)).Append("</td><td>").Append(m.Iteration?.ToString() ?? "—").Append("</td><td>").Append(Html.Esc(m.Label)).Append("</td><td>")
                  .Append(Html.Esc(m.Instrument)).Append("</td><td>").Append(m.Channel.HasValue ? "RV" + m.Channel : "—").Append("</td><td>").Append(m.Index).Append("</td><td>").Append(m.Value.HasValue ? Metrics.Fmt(m.Value.Value) : Html.Esc(m.Text)).Append("</td><td>").Append(Html.Esc(m.Unit)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (it.OperatorNotes.Count > 0)
        {
            sb.Append("<h4>操作员备注</h4><ul class=\"notes\">");
            foreach (var n in it.OperatorNotes) sb.Append("<li>").Append(Html.Esc(n)).Append("</li>");
            sb.Append("</ul>");
        }

        // 曲线
        if (samples != null && it.StartedAt.HasValue)
        {
            var from = it.StartedAt.Value; var to = it.EndedAt ?? from.AddHours(1);
            var series = new List<SvgSeries>();
            string[] palette = { SvgChart.LineTemp, "#7a8fa0", "#a0a890", "#8d8aa5" };
            int k = 0;
            foreach (var c in it.Channels)
            {
                var pv = samples.Series(c, "tempC", from, to);
                if (pv.Count > 0) series.Add(new SvgSeries($"RV{c} 温度", palette[k++ % palette.Length], pv, false));
            }
            if (it.Channels.Count > 0)
            {
                var sp = samples.Series(it.Channels[0], "tempSpC", from, to);
                if (sp.Count > 0) series.Add(new SvgSeries($"RV{it.Channels[0]} SP", SvgChart.LineSp, sp, false));
                var pr = samples.Series(it.Channels[0], "pressurePsi", from, to);
                if (pr.Count > 0) series.Add(new SvgSeries($"RV{it.Channels[0]} 压力", SvgChart.LinePress, pr, true));
                var rpm = samples.Series(it.Channels[0], "rpm", from, to);
                if (rpm.Count > 0 && rpm.Any(p => p.Y > 0)) series.Add(new SvgSeries("转速设定", SvgChart.LineSpeed, rpm, true));
            }
            var bands = it.Steps.Where(s => (s.Kind == nameof(StepKind.Hold) || s.Kind == nameof(StepKind.WaitStable)) && s.StartedAt.HasValue && s.EndedAt.HasValue)
                .Select(s => new SvgBand((s.StartedAt!.Value - from).TotalSeconds, (s.EndedAt!.Value - from).TotalSeconds, s.Title, s.Kind == nameof(StepKind.Hold) ? "#dcf8c7" : "#e5e0f5")).ToList();
            var markers = it.Steps.Where(s => s.Kind == nameof(StepKind.Command) && s.StartedAt.HasValue).Select(s => new SvgMarker((s.StartedAt!.Value - from).TotalSeconds, s.Title)).ToList();
            if (series.Count > 0)
            {
                sb.Append("<h4>曲线</h4><div class=\"chart-box\">").Append(SvgChart.Render(series, null, null, "测试时间 (mins)", "温度 (℃)", "压力 (psi) / 转速 (rpm)", bands, markers)).Append("</div>");
            }
        }
        sb.Append("</div>");
    }

    private static string RecordValue(RunResult r, ItemResult it, RecordField f, int iter, int ch)
    {
        var src = f.Source;
        if (src.StartsWith("metric:", StringComparison.Ordinal))
        {
            var key = src.Substring(7);
            var m = it.Measures.FirstOrDefault(x => x.Key == key && (x.Iteration ?? 1) == iter && (x.Channel == ch || x.Channel == null));
            return m == null ? "—" : Metrics.Fmt(m.Value);
        }
        if (src.StartsWith("manual:", StringComparison.Ordinal))
        {
            var key = src.Substring(7);
            var ms = it.ManualEntries.Where(x => x.Key == key && (x.Iteration ?? 1) == iter && (x.Channel == ch || x.Channel == null)).ToList();
            if (ms.Count == 0) return "—";
            return string.Join(" / ", ms.Select(x => x.Value.HasValue ? Metrics.Fmt(x.Value.Value) : x.Text ?? ""));
        }
        if (src.StartsWith("step:", StringComparison.Ordinal))
        {
            var id = src.Substring(5).Split('.')[0];
            var s = it.Steps.FirstOrDefault(x => x.StepId == id && (x.Iteration ?? 1) == iter);
            return s == null ? "—" : StepZh(s.Status);
        }
        return src switch
        {
            "run.channel" => ch == 0 ? "—" : "RV" + ch,
            "run.iteration" => iter.ToString(),
            "run.date" => it.StartedAt?.ToLocalTime().ToString("yyyy-MM-dd", Ci) ?? "—",
            "run.operator" => r.Operator,
            "run.runId" => r.RunId,
            "run.outcome" => OutcomeZh(it.Outcome),
            _ => "",
        };
    }

    private static void Meta(StringBuilder sb, string k, string v) => sb.Append("<div class=\"meta\"><span class=\"k\">").Append(Html.Esc(k)).Append("</span><span class=\"v\">").Append(Html.Esc(v)).Append("</span></div>");
    private static string Opt(double? v, string unit) => v.HasValue ? Metrics.Fmt(v.Value) + " " + unit : "—";
    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "—" : s.Length > 8 ? s.Substring(0, 8) : s;
    private static string Local(DateTime? t, bool timeOnly = false) => t.HasValue ? t.Value.ToLocalTime().ToString(timeOnly ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", Ci) : "—";
    public static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    public static string StatusZh(RunStatus s) => s switch
    {
        RunStatus.Pending => "待开始", RunStatus.Running => "运行中", RunStatus.Paused => "已暂停", RunStatus.WaitingConfirm => "等待手动确认",
        RunStatus.WaitingEntry => "等待人工录入", RunStatus.WaitingSafety => "安全暂停", RunStatus.Completed => "已完成", RunStatus.Aborted => "已中止", RunStatus.Failed => "失败", _ => s.ToString(),
    };
    public static string OutcomeZh(ItemOutcome o) => o switch { ItemOutcome.Completed => "完成", ItemOutcome.Aborted => "中止", ItemOutcome.Skipped => "跳过", ItemOutcome.Failed => "失败", _ => "未开始" };
    private static string OutcomeClass(ItemOutcome o) => o switch { ItemOutcome.Completed => "pass", ItemOutcome.Aborted or ItemOutcome.Failed => "fail", _ => "" };
    public static string StepZh(StepStatus s) => s switch
    {
        StepStatus.Done => "完成", StepStatus.Skipped => "跳过", StepStatus.Failed => "失败", StepStatus.Aborted => "中止", StepStatus.TimedOut => "超时",
        StepStatus.Running => "运行中", StepStatus.WaitingConfirm => "等待确认", StepStatus.WaitingEntry => "等待录入", _ => "待执行",
    };
    public static string KindZh(string k) => k switch
    {
        nameof(StepKind.Instruction) => "现场操作", nameof(StepKind.Checklist) => "检查表", nameof(StepKind.ManualEntry) => "人工录入", nameof(StepKind.Command) => "命令",
        nameof(StepKind.WaitStable) => "等待稳定", nameof(StepKind.Hold) => "定时记录", nameof(StepKind.Measure) => "计算", nameof(StepKind.Compare) => "设置核对",
        nameof(StepKind.SessionBegin) => "开始会话", nameof(StepKind.Marker) => "标记", nameof(StepKind.SessionEnd) => "结束会话", _ => k,
    };
    private static string VerdictZh(ItemResult it) => it.Verdict switch { Verdict.Pass => "合格", Verdict.Fail => "不合格", Verdict.Info => "信息", _ => "—（摸底）" };

    private static string SettingsSummary(System.Text.Json.JsonElement s)
    {
        try
        {
            string G(string a, string b) => s.TryGetProperty(a, out var o) && o.TryGetProperty(b, out var v) ? v.ToString() : "—";
            return $"TMaxSp {G("limits", "tempSpMaxC")} ℃ · RpmMax {G("limits", "rpmMax")} · 超压 {G("alarms", "overPressurePsi")} psi · 超温 {G("alarms", "overTempC")} ℃ · 逼近 Δ {G("approach", "deltaC")} ℃ · 泄漏测试 {G("leakTest", "testPsi")} psi · 满量程 {G("pressure", "fullScaleMPa")} MPa · 修订 {(s.TryGetProperty("revision", out var r) ? r.ToString() : "—")}";
        }
        catch { return s.ToString(); }
    }

    /// <summary>与 pc-control.html 同令牌。</summary>
    public const string Css = """
:root{--line:#c9c9cd;--soft-line:#dedee1;--panel:#f7f7f8;--ink:#3f3f45;--muted:#85858c;--purple:#765cc5;--green:#efffe4;--green-line:#dcf8c7;--blue:#6269df;--bg:#ececee;--pass:#5a8f3a;--fail:#b85360}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font-family:"Segoe UI",Arial,"PingFang SC","Microsoft YaHei","Noto Sans CJK SC",sans-serif;font-size:9px;line-height:1.45}
.page{max-width:1120px;margin:14px auto;padding:0 12px}
.panel{background:var(--panel);border:1px solid var(--line);margin-bottom:10px;overflow:hidden}
.panel-heading{height:24px;display:flex;align-items:center;gap:6px;padding:0 8px;background:#fafafb;border-bottom:1px solid #d0d0d4;font-size:9px}
.panel-heading strong{font-weight:600}.panel-heading .muted{color:#8c8c91;font-weight:400}
.meta-grid{display:grid;grid-template-columns:repeat(3,1fr);gap:0 14px;padding:6px 10px 2px}
.meta{display:flex;justify-content:space-between;border-bottom:1px solid var(--soft-line);padding:3px 0;font-size:8px}
.meta .k{color:var(--muted)}.meta .v{font-weight:600;text-align:right}
.sub{padding:5px 10px;font-size:8px;color:#55555c;border-bottom:1px solid var(--soft-line)}
.sub p{margin:3px 0}
h4{margin:8px 10px 3px;font-size:8.5px;font-weight:600;color:#55555c}
.report-table{width:calc(100% - 20px);margin:0 10px 8px;border-collapse:collapse;font-size:8px;background:#fff;border:1px solid var(--soft-line)}
.report-table th,.report-table td{padding:3px 5px;text-align:left;border-bottom:1px solid #e5e5e7;vertical-align:top}
.report-table th{font-weight:600;color:#55555c;background:#fafafb;white-space:nowrap}
.report-table td.pass,.pass{color:var(--pass)}.report-table td.fail,.fail{color:var(--fail)}
.muted{color:var(--muted)}
.chart-box{margin:0 10px 8px;background:#fafafa;border:1px solid var(--soft-line)}
.chart-box svg{display:block;width:100%;height:220px}
.notes{margin:2px 10px 8px;padding-left:16px;font-size:8px}
.banner-sim,.banner-abort{padding:6px 10px;margin-bottom:10px;font-size:10px;font-weight:600;border:1px solid var(--fail);color:var(--fail);background:#fff5f6}
.footer{color:var(--muted);font-size:7px;text-align:center;padding:8px 0 14px}
@media print{body{background:#fff}.page{margin:0;max-width:none}.panel{break-inside:avoid}}
""";
}
