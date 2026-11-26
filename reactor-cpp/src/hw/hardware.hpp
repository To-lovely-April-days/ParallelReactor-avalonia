// 按 hardware.json 组装整个设备层：4 条总线（各一个 I/O 线程）+ 4 个驱动 + 模拟主站。
// C#: Service/HardwareOptions.cs 的 CreateXxxBus / SimBus。
//
//   - 模拟（cfg.simulated()）：温控 / 压力 / 阀门三条总线共用一个 MockModbusMaster（8 路温控 + 8AI），搅拌单独一个；
//     控制线程每个周期调 simulate_step()。
//   - 真机：端口非空 → RtuModbusMaster；端口为空 → OfflineModbusMaster（模块离线，读数「—」）。
//   - 某条总线 trace 非空 → 外面包一层 TracingModbusMaster。
//
// 线程：make_hardware / stop 在启动 / 退出线程调；其余成员（总线、驱动、urgent_stop_all、simulate_step）任何线程可调。
#pragma once
#include <functional>
#include <memory>

#include "hw/analog_module.hpp"
#include "hw/bus.hpp"
#include "hw/hardware_config.hpp"
#include "hw/io_module.hpp"
#include "hw/mock_master.hpp"
#include "hw/stepper_driver.hpp"
#include "hw/temp_controller.hpp"

namespace reactor::hw {

// 「全部停止」快速路径的结果（每项 = 该总线上的停止帧全部写成功）。
struct StopAllOutcome {
    bool heat_ok = false;    // AI-8 8 路 At=4 全部成功
    bool valves_ok = false;  // IO16R 12 路全关成功
    bool stir_ok = true;     // DM2C 减速停成功（没要求停搅拌时恒 true）
    bool stir_requested = false;
};

struct Hardware {
    HardwareConfig cfg;
    bool simulated = false;
    bool sim_refused = false;  // 要求了模拟但配了端口 → 按真机跑

    std::shared_ptr<MockModbusMaster> sim_main;  // 模拟时：温控 + 压力 + 阀门共用
    std::shared_ptr<MockModbusMaster> sim_stir;  // 模拟时：搅拌

    std::unique_ptr<Bus> temp_bus, stir_bus, press_bus, io_bus;

    TempController temp;
    AnalogModule press;
    IoModule io;
    StepperDriver stir;

    // 模块是否「已配置」（C# UseRealXxx = Sim || 端口非空）。未配置的模块走离线主站。
    bool temp_configured() const { return simulated || !cfg.temp.port.empty(); }
    bool stir_configured() const { return simulated || !cfg.stir.port.empty(); }
    bool press_configured() const { return simulated || !cfg.press.port.empty(); }
    bool io_configured() const { return simulated || !cfg.io.port.empty(); }

    PressSignal press_signal() const { return cfg.press_4to20() ? PressSignal::Current4to20mA : PressSignal::Voltage0to10V; }

    // 模拟时推进一步（PV 漂移、压力抖动）；真机什么也不做。
    void simulate_step();

    // 「全部停止」快速路径第 1 步（设计文档 §4）：往紧急队列同时塞停止帧——
    // AI-8 逐路 At=4、IO16R 12 路全关、（stop_stir 时）DM2C 减速停。不锁存，不放空。
    // 立即返回；done（可空）在最后一笔做完的那条总线的 I/O 线程上调用一次。
    void urgent_stop_all(bool stop_stir, std::function<void(const StopAllOutcome&)> done = nullptr);

    // 停掉 4 条总线（排队中的事务回失败）。析构也会做。
    void stop();
    ~Hardware();
};

std::unique_ptr<Hardware> make_hardware(const HardwareConfig& cfg);

}  // namespace reactor::hw
