// 一个反应釜通道的运行状态（控制线程独占）。C#: Models/Reactor.cs 里控制相关的部分
#pragma once
#include <cstdint>
#include <optional>
#include <string>

#include "core/gas_uptake.hpp"
#include "core/heat_approach.hpp"
#include "core/reactor_state.hpp"
#include "core/temp_profile.hpp"

namespace reactor::ctl {

// 升温超时监视（设计文档 §6 新增的三级警告）
struct HeatWatch {
    bool active = false;
    double target = 0;
    double band = 2.0;
    int64_t since_ms = 0;
    bool reached = false;  // 已进入过到位范围
    bool warned = false;   // 已警告（只警告一次）
};

// 压力偏离监视（设计文档 §6）：充压完成、稳压开着时由第二阶段 begin_pressure_watch 打开
struct PressWatch {
    bool active = false;
    double target_psi = 0;
    std::optional<int64_t> out_since_ms;  // 连续偏离的起点
    bool warned = false;                  // 警告未解除
};

struct Channel {
    int id = 0;
    core::ReactorState state = core::ReactorState::Idle;
    bool over_temp = false;                 // 报警由超温引起（离开 Alarm 时清除；状态文字「超温」/「超压」）
    std::optional<std::string> started_by;  // "local" / 远程来源标签；停止 / 停用 / 全部停止时清空

    double t = 0, p = 0;                    // 最近有效读数（离线时保留旧值，用 t_online / p_online 判断）
    bool t_online = false, p_online = false;

    double tsp = 0;                         // 目标温度 ℃
    double psp = 0;                         // 压力给定 psi
    int rpm_sp = 200;
    double vol = 0;                         // 溶液体积 mL
    std::string sp_mode = "fixed";          // fixed / curve

    core::TempProfile profile;              // 抽屉 / 接口编辑的 16 段曲线
    core::HeatApproach approach;            // 定值防超调逼近
    core::GasUptakeMeter uptake;
    bool curve_write_in_flight = false;

    bool ot_idle_alarm = false;             // 超温报警发生时通道处于停用 / 已结束（C# _otIdleAlarm）
    HeatWatch heat_watch;
    PressWatch press_watch;

    void set_state(core::ReactorState s) {
        state = s;
        if (s != core::ReactorState::Alarm) over_temp = false;  // C# OnStateChanged
    }
};

}  // namespace reactor::ctl
