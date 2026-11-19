using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using ParallelReactor.Models;
using ParallelReactor.Services;
using ParallelReactor.ViewModels;

namespace ParallelReactor;

/// <summary>
/// <b>开发用临时钩子（只在 PR_SIM=1 模拟模式下生效；现场设备上没有任何作用）</b>：多程序并行的无头验收需要同时下发多个程序，
/// 而写接口（P3 `POST /channels/{id}/program/run`）尚未实现、触屏入口在 PR_HEADLESS 下不可操作，因此按环境变量
/// <c>PR_DEV_PROGRAMS</c> 在开机 delay 秒后经<b>命令层</b>（TryEnableChannel / TryRunProgram）下发程序——与触屏同一条路径。
/// 格式：<c>PR_DEV_PROGRAMS="1:exchange,hold;2:press,stir,react"</c>（通道:步骤类型列表，分号分隔多个通道；v0.8 步骤，结束自动补在最后），
/// 步骤用缩短的演示参数（置换 1 次 + 吹总管 1 次、保持 0.2 min、搅拌 300 rpm、反应 0.3 min、充压 50 psi 稳压开启）。
/// <c>PR_DEV_PROGRAMS_DELAY</c>：下发前等待秒数（默认 6）。<c>PR_DEV_ABORT="1@20"</c>：下发后 20 秒经 TryAbortProgram 中止 RV1（验证只中止该通道）。
/// 写接口落地后删除本文件。
/// </summary>
public static class DevHooks
{
    public static void Install(MainViewModel vm)
    {
        if (!HardwareOptions.Sim) return;
        var spec = Environment.GetEnvironmentVariable("PR_DEV_PROGRAMS");
        if (string.IsNullOrWhiteSpace(spec)) return;
        int delay = int.TryParse(Environment.GetEnvironmentVariable("PR_DEV_PROGRAMS_DELAY"), out var d) ? d : 6;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split(':', 2);
                if (kv.Length != 2 || !int.TryParse(kv[0], out int rv)) continue;
                var built = kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Build).Where(s => s != null).Select(s => s!).ToList();
                ProgramViewModel.PinEnd(built);   // 结束固定在最后（v0.8）
                for (int i = 0; i < built.Count; i++) built[i].Ordinal = i + 1;
                var steps = built.Select(s => s.ToSnapshot()).ToList();
                vm.Alarm.Log("info", "系统", $"开发钩子 · 下发 RV{rv} 程序", $"PR_DEV_PROGRAMS：{kv[1]}");
                vm.Local(vm.Ops.TryEnableChannel(MainViewModel.Src, rv, defaultVolume: true));
                vm.Local(vm.Ops.TryRunProgram(MainViewModel.Src, rv, steps.AsReadOnly()));
            }
            var abort = Environment.GetEnvironmentVariable("PR_DEV_ABORT");
            if (abort is { Length: > 0 } && abort.Split('@') is [var r1, var t1] && int.TryParse(r1, out int arv) && int.TryParse(t1, out int asec))
            {
                var at = new DispatcherTimer { Interval = TimeSpan.FromSeconds(asec) };
                at.Tick += (_, _) =>
                {
                    at.Stop();
                    vm.Alarm.Log("info", "系统", $"开发钩子 · 中止 RV{arv} 程序", "PR_DEV_ABORT");
                    vm.Local(vm.Ops.TryAbortProgram(MainViewModel.Src, arv));
                };
                at.Start();
            }
        };
        timer.Start();
    }

    private static ProgramStep? Build(string type) => type.ToLowerInvariant() switch
    {
        "leak" => ProgramStep.New(StepType.Leak).With("pres", 50.0).With("settle", 0.1).With("hold", 5.0),
        "exchange" => ProgramStep.New(StepType.Exchange).With("cycles", 1.0).With("lineFlush", 1.0),
        "purge" => ProgramStep.New(StepType.Exchange).With("cycles", 0.0).With("lineFlush", 3.0),   // 旧名：只吹总管
        "heat" => ProgramStep.New(StepType.Heat).With("temp", 40.0).With("wait", 0.1),
        "press" => ProgramStep.New(StepType.Press).With("pres", 50.0).With("regulate", "on"),
        "stir" => ProgramStep.New(StepType.Stir).With("rpm", 300.0),
        "stir2" => ProgramStep.New(StepType.Stir).With("rpm", 400.0),
        "hold" or "wait" => ProgramStep.New(StepType.Hold).With("until", "time").With("time", 0.2),
        "react" => ProgramStep.New(StepType.React).With("time", 0.3).With("minTime", 0.0).With("stallOn", "off"),
        "vent" => ProgramStep.New(StepType.Vent).With("flush", 0.0),
        "end" => ProgramStep.New(StepType.End),
        _ => null,
    };
}
