// 开始自动气路序列（程序吹扫 / 升压、泄漏测试、手动阀控路径）前的公共检查。C#: Models/GasGuard.cs
// 返回 nullopt = 可以开始，否则为（错误码, 与 C# 逐字相同的中文原因）。判断顺序与 C# 一致。
// 与 C# 的差别：急停已删除（设计文档 §6），不再有 EstopLocked 分支。
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "core/op_codes.hpp"
#include "core/reactor_state.hpp"

namespace reactor::core {

// 气路检查用的通道快照。idle_over_temp_alarm：停用 / 已结束通道的超温切断报警（不阻塞气路）。
struct GasChannel {
    int id = 0;
    ReactorState state = ReactorState::Idle;
    bool over_pressure_tripped = false;
    bool idle_over_temp_alarm = false;
};

struct GasGuardInput {
    bool io_configured = false;      // 阀门模块已配置串口
    bool need_pressure = false;      // 序列依赖压力读数（泄漏测试要；吹扫不要）
    bool analog_configured = false;
    bool press_online = false;
    std::string gas_task;            // 当前占用气路的序列名，空 = 空闲
    bool manual_valve = false;
    std::optional<int> self_id;      // 发起序列的程序自己的通道：它升压 / 反应不算被占用；报警仍阻塞
    std::vector<GasChannel> channels;
    bool gas_held_by_program = false;   // 当前气路占用者是程序
    bool wait_instead_of_busy = false;  // 发起方是程序气路步骤：被另一程序占用时不拒绝（排队）
    std::vector<int> program_owned_ids; // 其他正在执行程序的通道：升压 / 反应不阻塞（报警仍阻塞）
    std::string program_gas_pending;    // 程序方面的隐性占用说明（「RV1 程序 · 稳压」），空 = 没有
};

std::optional<GuardReject> gas_guard_check(const GasGuardInput& s);

}  // namespace reactor::core
