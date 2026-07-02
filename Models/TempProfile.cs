using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>曲线升温的一个程序段：在 <see cref="Minutes"/> 分钟内线性变化到 <see cref="Temp"/> ℃。</summary>
public partial class ProfileSegment : ObservableObject
{
    public int Index { get; }                       // 段号 1..16
    [ObservableProperty] private double _temp;      // 段目标温度 ℃
    [ObservableProperty] private double _minutes;   // 段时长 分钟
    [ObservableProperty] private bool _enabled;     // 是否在启用段数范围内（控制行显隐）
    [ObservableProperty] private bool _active;      // 是否为当前正在执行的段（高亮）

    public ProfileSegment(int index, double temp, double minutes)
    { Index = index; _temp = temp; _minutes = minutes; }
}

/// <summary>
/// 16 段曲线升温程序（每个反应釜一份）。
/// <para>
/// AI-8 无原生多段程序功能（仅 4 组共享的单段斜率），因此曲线由上位机实现：
/// 启动后按分段线性插值计算当前给定值，周期性写 SP 寄存器，仪表 PID 跟随。
/// 段 1 从启动时的实测 PV 开始，段 N 从段 N-1 的目标温度开始。走完保持最后一段温度。
/// </para>
/// </summary>
public partial class TempProfile : ObservableObject
{
    public const int MaxSegments = 16;

    public ObservableCollection<ProfileSegment> Segments { get; } = new();

    [ObservableProperty] private int _segCount = 4;      // 启用段数 1..16
    [ObservableProperty] private bool _running;
    [ObservableProperty] private string _statusText = "未启动";

    // —— 运行态 ——
    private double _startTemp;      // 启动时 PV（段 1 起点）
    private DateTime _startUtc;
    private double _lastWritten = double.NaN;   // 上次下发的 SP（去抖：变化≥0.1 才写）

    public TempProfile()
    {
        for (int i = 1; i <= MaxSegments; i++)
            Segments.Add(new ProfileSegment(i, 50, 10));
        ApplyEnabled();
    }

    partial void OnSegCountChanged(int value) => ApplyEnabled();

    private void ApplyEnabled()
    {
        int n = Math.Clamp(SegCount, 1, MaxSegments);
        foreach (var s in Segments) s.Enabled = s.Index <= n;
    }

    /// <summary>启动曲线：记录起点 PV 与时刻。</summary>
    public void Start(double currentPv)
    {
        _startTemp = currentPv;
        _startUtc = DateTime.UtcNow;
        _lastWritten = double.NaN;
        Running = true;
        StatusText = "第 1/" + SegCount + " 段";
    }

    /// <summary>停止曲线（不再下发，仪表保持最后写入的 SP）。</summary>
    public void Stop()
    {
        Running = false;
        foreach (var s in Segments) s.Active = false;
        StatusText = "已停止";
    }

    /// <summary>
    /// 按已运行时间计算当前应写入的给定值（分段线性插值）。
    /// 曲线走完返回最后一段温度并把状态置为完成（保持）。
    /// </summary>
    public double CurrentTarget()
    {
        double elapsedMin = (DateTime.UtcNow - _startUtc).TotalMinutes;
        double from = _startTemp, acc = 0;
        int n = Math.Clamp(SegCount, 1, MaxSegments);

        for (int i = 0; i < n; i++)
        {
            var seg = Segments[i];
            double dur = Math.Max(0.01, seg.Minutes);
            if (elapsedMin < acc + dur)
            {
                foreach (var s in Segments) s.Active = s.Index == i + 1;
                double remain = (acc + dur) - elapsedMin;
                StatusText = $"第 {i + 1}/{n} 段 · 剩 {remain:0.#} 分";
                double k = (elapsedMin - acc) / dur;
                return from + (seg.Temp - from) * k;
            }
            acc += dur;
            from = seg.Temp;
        }

        // 全部走完：保持最后一段温度
        foreach (var s in Segments) s.Active = false;
        StatusText = $"已完成 · 保持 {from:0.#} ℃";
        return from;
    }

    /// <summary>去抖判断：目标与上次写入相差 ≥0.1℃ 才需要下发。</summary>
    public bool ShouldWrite(double target)
    {
        if (!double.IsNaN(_lastWritten) && Math.Abs(target - _lastWritten) < 0.1) return false;
        _lastWritten = target;
        return true;
    }
}
