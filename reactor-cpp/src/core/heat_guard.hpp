// 启动加热（定值 / 曲线 / 自整定 / 程序加热步骤）前的公共检查。C#: Models/HeatGuard.cs
// 返回 nullopt = 可以启动，否则为（错误码, 与 C# 逐字相同的中文原因）。判断顺序与 C# 一致。
// 与 C# 的差别：急停已删除（设计文档 §6），不再有 EstopLocked 分支。
#pragma once
#include <optional>

#include "core/op_codes.hpp"
#include "core/units.hpp"

namespace reactor::core {

struct HeatGuardInput {
    std::optional<int> id;              // 要启动的通道（1 起）；nullopt = 只查全局项
    bool temp_configured = false;       // 温控已配置串口
    bool temp_online = false;
    bool over_temp_tripped = false;
    bool over_pressure_tripped = false;
    bool stop_heat_on_over_pressure = false;
    double over_temp = 0;               // 超温报警值 ℃
    double over_pressure = 0;           // 超压报警值 psi
    PressureUnit pressure_unit = PressureUnit::MPa;  // 文案里的压力显示单位（C# 用全局 Units.Pressure）
};

std::optional<GuardReject> heat_guard_check(const HeatGuardInput& s);

}  // namespace reactor::core
