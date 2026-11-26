#include "core/gas_guard.hpp"
#include "core/chan_name.hpp"

#include <algorithm>

namespace reactor::core {

namespace {
std::string join_rv(const std::vector<int>& ids) {
    return chan_names(ids);
}
}  // namespace

std::optional<GuardReject> gas_guard_check(const GasGuardInput& s) {
    if (!s.io_configured)
        return GuardReject{op_codes::kHwNotConfigured, "阀门模块未配置串口（PR_IO_PORT），无法执行气路操作"};
    if (s.need_pressure && !s.analog_configured)
        return GuardReject{op_codes::kHwNotConfigured, "压力采集未配置串口（PR_PRESS_PORT），无法闭环充压与超压监视"};
    if (s.need_pressure && !s.press_online)
        return GuardReject{op_codes::kHwOffline, "压力采集离线（8AI 通讯失败），请检查接线与串口后再试"};
    if (!s.gas_task.empty() && !(s.wait_instead_of_busy && s.gas_held_by_program))
        return GuardReject{op_codes::kGasBusy, s.gas_task + "进行中，请等待完成或先停止"};
    // 程序稳压脉冲间隙 / 排队等待期间，气路对泄漏测试与手动阀控仍视为占用
    if (!s.program_gas_pending.empty() && !s.wait_instead_of_busy)
        return GuardReject{op_codes::kGasBusy, s.program_gas_pending + "进行中，请等待完成或先停止"};
    if (s.manual_valve)
        return GuardReject{op_codes::kManualValveActive, "请先关闭「手动阀控」：自动序列与手动阀操作不能同时进行"};

    std::vector<int> op;
    for (auto& r : s.channels)
        if (r.over_pressure_tripped) op.push_back(r.id);
    if (!op.empty())
        return GuardReject{op_codes::kChannelAlarm, join_rv(op) + " 超压未解除（需低于超压报警值的 98%），禁止气路操作"};

    // 停用 / 已结束通道的超温切断报警不阻塞气路。程序气路步骤：其他通道的报警（非超压，上面已判）不阻塞；本通道报警仍阻塞。
    bool program_wait = s.wait_instead_of_busy;
    auto owned = [&](int id) {
        return std::find(s.program_owned_ids.begin(), s.program_owned_ids.end(), id) != s.program_owned_ids.end();
    };
    bool has_self = s.self_id.has_value();
    int self = s.self_id.value_or(0);
    std::vector<int> busy;
    for (auto& r : s.channels) {
        bool alarm = r.state == ReactorState::Alarm && (!program_wait || (has_self && r.id == self));
        bool press = (r.state == ReactorState::Pressing || r.state == ReactorState::React) &&
                     !(has_self && r.id == self) && !owned(r.id);
        if ((alarm || press) && !r.idle_over_temp_alarm) busy.push_back(r.id);
    }
    if (!busy.empty())
        return GuardReject{op_codes::kChannelPressurized, join_rv(busy) + " 正在加压 / 反应 / 报警，需先停止"};
    return std::nullopt;
}

}  // namespace reactor::core
