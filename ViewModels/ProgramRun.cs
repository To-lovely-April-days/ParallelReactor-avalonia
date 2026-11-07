using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 一次程序执行（每通道一份，最多 8 份并行）：下发时冻结的步骤副本、监视列表、流程令牌、后台稳压回路句柄、
/// 中止 / 收尾用的标记，以及对外（API / 运行记录）只读的 <see cref="Info"/>。
/// 界面绑定的 RunStatus / RunMonitor* 是 ProgramViewModel 对「选中通道的运行」的投影，这里是数据源。
/// </summary>
public sealed partial class ProgramRun : ObservableObject
{
    public ProgramRun(int rv, IReadOnlyList<StepSnapshot> steps, string startedBy)
    {
        Rv = rv;
        Steps = steps;
        Info = new ProgramRunInfo(rv, steps.Count, startedBy, DateTimeOffset.Now);
        int index = 1;
        foreach (var step in steps) RunSteps.Add(new ProgramRunStep(index++, step));
        _progressText = $"0 / {steps.Count}";
        _monitorDetail = $"RV{rv} 程序正在准备启动";
    }

    public int Rv { get; }
    public IReadOnlyList<StepSnapshot> Steps { get; }
    public ObservableCollection<ProgramRunStep> RunSteps { get; } = new();
    public ProgramRunInfo Info { get; }

    /// <summary>流程令牌：中止按钮 / 全部停止 / 急停 / 抽屉停止经 AbortProgram(rv) 取消。</summary>
    public CancellationTokenSource Cts { get; } = new();
    /// <summary>后台稳压回路（升压步骤 regulate=开启）。</summary>
    internal CancellationTokenSource? RegCts;
    internal Task? RegTask;
    /// <summary>中止时已发出的停加热任务（finally 复用，避免重复串口帧与重复提示）。</summary>
    internal Task? AbortOutputsTask;
    /// <summary>
    /// 中止指令（含其停加热）发出之后，执行流程又碰过加热输出（例如曲线升温的全链启动恰好在中止期间完成）：
    /// finally 不能复用中止时的停加热任务，必须重新下发一次。
    /// </summary>
    internal bool HeatTouchedAfterAbort;
    /// <summary>本程序在 StirShare 里有一份搅拌请求（搅拌步骤进行中）。</summary>
    internal bool StirHeld;
    /// <summary>后台稳压回路判定的程序故障（如等待气路超时）：取消流程令牌后由 ExecuteAsync 按失败而非手动中止收尾。</summary>
    internal string? PendingFault;
    /// <summary>本程序最近一次取到的气路租约代号（收尾只释放自己这段；0 = 还没取过）。</summary>
    internal long GasGen;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _monitorPhase = "准备执行";
    [ObservableProperty] private string _monitorDetail;
    [ObservableProperty] private string _progressText;
    [ObservableProperty] private int _currentIndex = -1;

    public bool IsActive => Info.IsActive;

    partial void OnStatusChanged(string value)
    {
        Info.Status = value;
        if (!string.IsNullOrWhiteSpace(value)) MonitorDetail = value;
    }

    partial void OnMonitorDetailChanged(string value) => Info.Message = value;
    partial void OnMonitorPhaseChanged(string value) => Info.PhaseText = value;
    partial void OnCurrentIndexChanged(int value) => Info.Index = value;

    /// <summary>切换阶段并通知 <see cref="IsActive"/>（投影属性 IsRunningProgram 由 ProgramViewModel 转发）。</summary>
    internal void SetPhase(ProgramRunPhase phase)
    {
        if (Info.Phase == phase) return;
        Info.Phase = phase;
        OnPropertyChanged(nameof(IsActive));
    }

    internal void MarkStepStarted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        CurrentIndex = index;
        RunSteps[index].SetState(ProgramRunState.Running, "正在执行");
        ProgressText = $"{index} / {RunSteps.Count}";
        MonitorPhase = $"执行第 {index + 1} 步 · {RunSteps[index].Name}";
    }

    internal void MarkStepCompleted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Completed, "已完成");
        ProgressText = $"{index + 1} / {RunSteps.Count}";
    }

    internal void MarkStepFailed(int index, string message)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Failed, message);
        MonitorPhase = $"第 {index + 1} 步执行失败";
    }

    internal void MarkStepAborted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Aborted, "已中止");
        MonitorPhase = "程序已手动中止";
    }

    /// <summary>当前步骤进入 / 退出「等待气路」：步骤仍为执行中（样式不变），只换文字。reason：括号内文字（「RV2 占用中」/ 公共检查不通过的原因）。</summary>
    internal void MarkStepWaitingGas(string? reason)
    {
        int i = CurrentIndex;
        if (i < 0 || i >= RunSteps.Count) return;
        if (reason != null)
        {
            SetPhase(ProgramRunPhase.WaitingGas);
            RunSteps[i].SetState(ProgramRunState.Running, $"等待气路（{reason}）");
            MonitorPhase = $"第 {i + 1} 步 · 等待气路";
        }
        else
        {
            SetPhase(ProgramRunPhase.Running);
            Info.WaitingOn = null;
            RunSteps[i].SetState(ProgramRunState.Running, "正在执行");
            MonitorPhase = $"执行第 {i + 1} 步 · {RunSteps[i].Name}";
        }
    }
}
