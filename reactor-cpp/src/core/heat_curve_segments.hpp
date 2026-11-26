// 升温步骤（目标温度 / 升温斜率 / 保温时长）→ 曲线引擎分段（温度, 分钟）。C#: Models/HeatCurvePlan.cs
// 每段 → 斜坡段（分钟 = |ΔT|/rate；rate=0 且变温按 0.01 分钟阶跃；|ΔT|<0.01 无斜坡）+ 保温段（hold 分钟，>0 才有）。
// start_temp = 启动时实测 PV。超 max 段截断；无有效段返回空。
#pragma once
#include <vector>

#include "core/temp_profile.hpp"

namespace reactor::core {

struct HeatSegment {
    double temp = 0;  // 目标温度 ℃
    double rate = 0;  // 升温斜率 ℃/min
    double hold = 0;  // 保温 分钟
};

std::vector<ProfileSegment> heat_curve_to_profile(const std::vector<HeatSegment>& segs, double start_temp,
                                                  int max = temp_profile::kMaxSegments);

}  // namespace reactor::core
