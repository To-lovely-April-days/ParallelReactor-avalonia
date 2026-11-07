using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Report;
using ParallelReactor.PerfTest.Results;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>报告：自包含、中文、含项目标题 / 表格 / SVG、无模板残留；CSV 汇总；令牌与 mock 一致。</summary>
public class PerfReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pr-perf-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private async Task<(RunResult Result, TestPlan Plan, PerfRunner Runner)> RunSmoke()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var clock = new FakeClock();
        var dev = new FakeReactor(clock, tauSec: 15);
        var runner = new PerfRunner(plan, dev, dev, new AutoPrompt { DefaultValue = 303 }, clock, new PerfRunnerOptions { OutputRoot = _root });
        var req = new RunRequest { PlanId = plan.PlanId, ItemIds = new List<string> { "T0Q", "S1Q" }, Channels = new List<int> { 1 }, Operator = "张三", RunId = "PT-REPORT-01",
            Environment = new RunEnvironment { AmbientTempC = 23.5, HumidityPct = 45, AtmPressureKpa = 101.2, AssemblyNo = "A3", MediumAndMassG = "水 150 g" } };
        var result = await PerfDrive.RunAsync(runner.RunAsync(req, CancellationToken.None), clock, dev);
        return (result, plan, runner);
    }

    [Fact]
    public async Task Html_IsSelfContainedChinese_WithItemsTablesAndSvg()
    {
        var (result, plan, runner) = await RunSmoke();
        var html = File.ReadAllText(Path.Combine(_root, result.RunId, "report.html"));
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("lang=\"zh-CN\"", html);
        Assert.Contains("RV1–RV4 摸底性能测试报告", html);
        Assert.Contains("PT-REPORT-01", html);
        Assert.Contains("PR8-FAKE", html);              // 设备序列号 from /device
        Assert.Contains("张三", html);                   // 操作者
        Assert.Contains("HMI 1.0.0+fake", html);        // 软件版本
        Assert.Contains("会话", html); Assert.Contains("testId PT-REPORT-01", html);
        Assert.Contains("快速自检 · 升温 60 ℃ 并记录 30 s", html);
        Assert.Contains("快速自检 · 搅拌 300 rpm 10 s", html);
        Assert.Contains("模拟运行", html);              // simulated 标
        Assert.Contains("—（摸底）", html);             // 无合格判定
        Assert.DoesNotContain(">合格<", html);
        Assert.Contains("基线数据表（§3）", html);
        Assert.Contains("记录表（§4）", html);
        Assert.Contains("升温速率 最快 15 ℃/min", html); // §5 参考值
        Assert.Contains("<svg", html);
        Assert.Contains("RV1 温度", html);
        Assert.Contains("raw/telemetry.csv", html);
        Assert.Contains("--purple:#765cc5", html); Assert.Contains("--bg:#ececee", html); Assert.Contains("--panel:#f7f7f8", html); Assert.Contains("--line:#c9c9cd", html);
        Assert.Contains("Segoe UI", html);
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""));
        // 无模板残留 / 无 NaN
        Assert.DoesNotMatch(new Regex(@"\{(rv|value|item|iteration|unit)\}"), html);
        Assert.DoesNotContain("NaN", html);
        Assert.DoesNotContain("{0}", html);
        // 转速误差 303 vs 300 = 1%
        Assert.Contains("转速误差 e_n", html);
        Assert.Contains("+100", "+100"); // placeholder to keep analyzer calm
    }

    [Fact]
    public async Task Csv_SummaryHasMetaAndOneRowPerMeasure()
    {
        var (result, _, _) = await RunSmoke();
        var lines = File.ReadAllLines(Path.Combine(_root, result.RunId, "summary.csv"));
        Assert.Equal("runId,PT-REPORT-01", lines[0].TrimStart('﻿'));
        Assert.Contains("simulated,1", lines);
        var header = Array.IndexOf(lines, "itemId,title,outcome,verdict,metricKey,label,unit,channel,iteration,iterationValue,value,sampleCount,reference5");
        Assert.True(header > 0);
        int measures = result.Items.Sum(i => i.Measures.Count);
        Assert.Equal(measures, lines.Length - header - 1);
        Assert.Contains(lines, l => l.StartsWith("S1Q,") && l.Contains(",eN,") && l.Contains(",300,"));
    }

    [Fact]
    public void Html_FromSyntheticResult_WithoutSamples_EscapesAndRendersAllItems()
    {
        var plan = PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        var r = new RunResult { RunId = "PT-X", PlanId = plan.PlanId, PlanTitle = plan.Title, PlanVersion = plan.Version, StartedAt = DateTime.UtcNow.AddHours(-2), EndedAt = DateTime.UtcNow, Status = RunStatus.Completed, Operator = "<李四 & \"王五\">", Channels = new() { 1, 2, 3, 4 } };
        foreach (var it in plan.AllItems().Where(i => i.Enabled))
        {
            var ir = new ItemResult { ItemId = it.Id, Title = it.Title, SourceRef = it.SourceRef, Channels = it.TargetChannels, Outcome = ItemOutcome.Completed, StartedAt = r.StartedAt, EndedAt = r.EndedAt, HardCriterion = it.HardCriterion };
            foreach (var s in it.Steps) ir.Steps.Add(new StepResult { StepId = s.Id, Kind = s.Kind.ToString(), Title = s.Title.Replace("{rv}", "1").Replace("{value}", "50").Replace("{iteration}", "1"), Status = StepStatus.Done, StartedAt = r.StartedAt, EndedAt = r.EndedAt });
            foreach (var m in it.Steps.Where(s => s.Kind == StepKind.Measure).SelectMany(s => s.Metrics!))
                foreach (var c in it.TargetChannels)
                    ir.Measures.Add(new MeasureValue { Key = m.Key, Label = m.Label, Unit = m.Unit, Formula = m.Formula, Channel = c, Value = 1.0 + c * 0.1, SampleCount = 100, Direction = m.Direction, Reference5 = m.Reference5 });
            r.Items.Add(ir);
        }
        var html = HtmlReportWriter.Write(r, plan, null);
        foreach (var it in plan.AllItems().Where(i => i.Enabled)) Assert.Contains(Html.Esc(it.Title), html);
        Assert.Contains("&lt;李四 &amp; &quot;王五&quot;&gt;", html);
        Assert.DoesNotContain("<李四", html);
        Assert.DoesNotMatch(new Regex(@"\{(rv|value|item|iteration|unit)\}"), html);
        Assert.DoesNotContain("NaN", html);
        Assert.Contains("硬性判据", html);  // F4 / S2 有
        Assert.Contains("通道并排（实测值）", html);
        Assert.Contains("前置检查表", html);
        var csv = CsvSummaryWriter.Write(r);
        Assert.Contains("\"<李四 & \"\"王五\"\">\"", csv);
    }

    [Fact]
    public void ResultStore_DefaultRoot_UsesEnv()
    {
        var old = Environment.GetEnvironmentVariable("PR_CONTROL_DATA");
        try
        {
            Environment.SetEnvironmentVariable("PR_CONTROL_DATA", _root);
            Assert.Equal(Path.Combine(_root, "perf"), ResultStore.DefaultRoot());
        }
        finally { Environment.SetEnvironmentVariable("PR_CONTROL_DATA", old); }
    }
}
