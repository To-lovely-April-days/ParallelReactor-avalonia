using System;
using System.Collections.Generic;

namespace ParallelReactor.Models;

/// <summary>
/// 升温步骤（目标温度 / 升温斜率 / 保温时长）→ 曲线引擎分段（温度, 分钟）的纯逻辑换算。
/// 无 UI 依赖（测试项目直接编译本文件）：每段 → 斜坡段（分钟 = |ΔT|/rate；rate=0 且变温按 0.01 分钟阶跃）
/// + 保温段（Hold 分钟）。startTemp = 启动时实测 PV（斜坡从它起算）。超 max 段截断；无有效段返回空列表。
/// </summary>
public static class HeatCurvePlan
{
    public static List<(double Temp, double Minutes)> ToProfileSegments(
        IReadOnlyList<(double Temp, double Rate, double Hold)> segs, double startTemp, int max = 16)
    {
        var conv = new List<(double, double)>();
        double prev = startTemp;
        foreach (var s in segs)
        {
            if (conv.Count >= max) break;
            double dt = Math.Abs(s.Temp - prev);
            double ramp = dt < 0.01 ? 0 : s.Rate > 0 ? dt / s.Rate : 0.01;
            if (ramp > 0) conv.Add((s.Temp, ramp));
            prev = s.Temp;
            if (conv.Count < max && s.Hold > 0) conv.Add((s.Temp, s.Hold));
        }
        return conv;
    }
}
