// 无硬件时的内存模拟主站。C#: Hardware/MockModbusMaster.cs
//
// 保存写入值、返回存储值（没写过的地址读出 0）；simulate_step() 让数据「活」起来：
//   - 温控（保持寄存器）：PV 每步向 SP 靠近最多 0.5 ℃（5 个原码）再加 ±1 原码噪声；At=4 或 Srun=9655 时目标改成室温 25.0 ℃；
//     At=1（自整定）持续 6 步后回 0，并写入一组随机 PID。首次调用时预置 PID / Ctn / Srun。
//   - 压力（输入寄存器）：gas_model=false（默认，同 C#）时每路原码在 8000 附近 ±30 抖动，夹在 3200..16000。
//     gas_model=true（reactord --sim 用）时按「总管 + 8 釜」理想气体模型算表压（同 tests/ctl 的 Physics）：
//     阀全关时釜压保持（常压 0 psi = 4 mA 原码 3200，±1 原码抖动）；开气源 → 总管到减压阀压力；开排空 → 总管到 0；
//     开 SVn → 釜与总管交换。釜热（PV > 60 ℃）且充过气体 A/B 时按一级反应缓慢吸气（压力下降，给吸气量曲线用）。
//     模型按真实时间积分（steady_clock），在读压力 / 写阀门 / simulate_step 时推进到「现在」。
//   - 阀门（线圈）：只存不变。
// 生产配置下温控 / 压力 / 阀门共用一个实例（三者分别用保持寄存器 / 输入寄存器 / 线圈，互不冲突），搅拌单独一个。
//
// 测试用后门：直接读写内存（holding / set_holding …）、注入失败（set_fail_all / fail_next）。
// 线程：全部方法线程安全（单锁），可以被多条 Bus 同时使用。
#pragma once
#include <cstdint>
#include <mutex>
#include <random>
#include <string>
#include <unordered_map>
#include <vector>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

class MockModbusMaster : public ModbusMaster {
public:
    struct Options {
        std::vector<uint8_t> temp_slaves;  // AI-8 站号（PV 模拟用）；空 = 不模拟温控
        int temp_loops = 4;                // C# 默认 4；生产用 8（HardwareOptions.SimBus TempLoops = 8）
        uint8_t analog_slave = 0;          // 8AI 站号，0 = 不模拟压力
        uint32_t seed = 0;                 // 随机种子，0 = 随机（测试时给固定值）
        // ---- 气路模型 + 加热输出 OP（只给 reactord --sim 用；单元测试保持 C# 的随机游走）----
        bool gas_model = false;
        uint8_t io_slave = 1;              // IO16R 站号（线圈 0-7 = SV1-8，8 惰性，9 气体 A，10 气体 B，11 排空）
        bool io_energize_opens = true;     // 同 hardware.json io.energize
        double fullscale_psi = 600;        // 原码 16000 = 满量程
        bool press_4to20 = true;           // 4-20 mA：原码 3200 = 0 psi；0-10 V：原码 0 = 0 psi
        double supply_psi[3] = {480, 480, 480};  // 惰性 / A / B 减压阀出口压力（表压 psi，≈ 3.3 MPa，够内置模板的 3 MPa 气密）
    };

    MockModbusMaster();
    explicit MockModbusMaster(Options opt);

    ModbusResult execute(const ModbusRequest& req) override;

    // 每个控制周期调用一次，推进模拟值。
    void simulate_step();

    // ---- 后门（测试 / 开发） ----
    uint16_t holding(uint8_t slave, uint16_t addr) const;
    void set_holding(uint8_t slave, uint16_t addr, uint16_t v);
    uint16_t input(uint8_t slave, uint16_t addr) const;
    void set_input(uint8_t slave, uint16_t addr, uint16_t v);
    bool coil(uint8_t slave, uint16_t addr) const;
    void set_coil(uint8_t slave, uint16_t addr, bool v);
    void set_discrete(uint8_t slave, uint16_t addr, bool v);
    // 气路模型：按给定秒数积分（测试用，不看真实时间），然后刷新压力原码
    void simulate_gas(double sec);

    // 之后每笔事务都失败（模拟断线）；false 恢复。
    void set_fail_all(bool fail);
    // 接下来 n 笔事务失败。
    void fail_next(int n);
    // 已执行的事务数（含失败）。
    int64_t txn_count() const;
    // 最近一笔请求（测试看下发了什么）。
    ModbusRequest last_request() const;
    // 全部写请求按顺序记录（测试用；clear_writes 清空）。
    std::vector<ModbusRequest> writes() const;
    void clear_writes();

private:
    static uint32_t key(uint8_t s, uint16_t a) { return (static_cast<uint32_t>(s) << 16) | a; }
    int rnd(int lo, int hi_exclusive);  // 同 C# Random.Next(lo, hi)：lo..hi-1
    void seed_temp();
    void advance_gas_locked();          // 气路模型积分到现在（持锁调用）
    void integrate_gas_locked(double dt_total);
    void publish_pressure_locked();     // 釜压 → 输入寄存器原码（持锁调用）

    Options opt_;
    mutable std::mutex mu_;
    std::unordered_map<uint32_t, uint16_t> hold_, input_;
    std::unordered_map<uint32_t, bool> coil_, di_;
    std::unordered_map<uint32_t, int> tune_count_;
    std::mt19937 rng_;
    bool seeded_ = false;
    // 气路模型状态（表压 psi）
    double pm_ = 0;                     // 总管
    double p_[8] = {};                  // 釜
    double uptake_left_[8] = {};        // 还能吸收多少 psi（放空到常压后重置）
    bool reactive_[8] = {};             // 充过气体 A/B（可以吸气）
    int64_t gas_t_us_ = -1;
    bool fail_all_ = false;
    int fail_next_ = 0;
    int64_t txns_ = 0;
    ModbusRequest last_;
    std::vector<ModbusRequest> writes_;
};

}  // namespace reactor::hw
