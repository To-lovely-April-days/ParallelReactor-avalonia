// 艾莫迅 JY-MODBUS-IO16R：16 路 DO 线圈 + 16 路 DI。C#: Hardware/IoModule.cs + Service/ValveService.cs 的寄存器部分
// 线圈：0x01 读 / 0x05 写单（FF00 闭合，0000 断开）/ 0x0F 写多；离散输入 0x02。
// 阀门映射（C# ValveService）：线圈 0–7 = SV1–8（RV1–8 进气阀），8 = 惰性气源，9 = 气体 A，10 = 气体 B，11 = 排空。
// 「开 / 关」与「通电 / 断电」：energize_opens=true（默认）通电 = 开；常开阀设 false（hardware.json io.energize = "close"）。
//
// 不做的事（留给 ctl）：急停锁（产品已删）、SV 打开计数（吸气量计量用，C# GetSvOpenGen）、在线统计。
// 对象不可变，任何线程可用。
#pragma once
#include <array>
#include <cstdint>
#include <optional>
#include <vector>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

class IoModule {
public:
    static constexpr int kCoils = 16;
    static constexpr int kValveCount = 12;
    static constexpr int kSvBase = 0;     // RV1 进气阀 = 线圈 0
    static constexpr int kGasBase = 8;    // 惰性 = 8, A = 9, B = 10
    static constexpr int kGasInert = 8;
    static constexpr int kGasA = 9;
    static constexpr int kGasB = 10;
    static constexpr int kVent = 11;

    using Valves = std::array<bool, kValveCount>;  // [0..7] SV1–8，[8..10] 惰性/A/B，[11] 排空；true = 开

    explicit IoModule(uint8_t slave = 1, bool energize_opens = true) : slave_(slave), energize_opens_(energize_opens) {}
    uint8_t slave() const { return slave_; }
    bool energize_opens() const { return energize_opens_; }

    bool level(bool open) const { return energize_opens_ ? open : !open; }   // 「希望开」→ 线圈电平
    bool open_from(bool coil) const { return energize_opens_ ? coil : !coil; }

    // ---- 原始线圈（不经极性） ----
    ModbusRequest req_read_coils() const { return req_read_coils_raw(0, kCoils); }
    ModbusRequest req_read_inputs() const { return req_read_discrete_inputs(slave_, 0x0000, kCoils); }
    ModbusRequest req_set_coil(int index, bool on) const { return req_write_coil(slave_, static_cast<uint16_t>(index), on); }
    ModbusRequest req_set_coils(int start, std::vector<bool> levels) const {
        return req_write_coils(slave_, static_cast<uint16_t>(start), std::move(levels));
    }

    // ---- 阀门（经极性） ----
    ModbusRequest req_set_valve(int coil_index, bool open) const { return req_set_coil(coil_index, level(open)); }
    ModbusRequest req_set_sv(int reactor_id, bool open) const { return req_set_valve(kSvBase + reactor_id - 1, open); }  // 1..8
    ModbusRequest req_set_gas(int index, bool open) const { return req_set_valve(kGasBase + index, open); }  // 0 惰性 1 A 2 B
    ModbusRequest req_set_vent(bool open) const { return req_set_valve(kVent, open); }
    // 一帧（0x0F，从线圈 0 起 12 路）同时写 12 路阀门。C#: ValveService.WriteAllAsync
    ModbusRequest req_write_valves(const Valves& open) const;
    // 只关不开：一帧把 start..start+count-1 写成「关」，范围外不碰。C#: ValveService.CloseRangeAsync
    ModbusRequest req_close_range(int start, int count) const;
    ModbusRequest req_close_gas_sources() const { return req_close_range(kGasBase, 3); }
    // 12 路全关（含排空）——「全部停止」快速路径用的帧
    ModbusRequest req_close_all_valves() const { return req_close_range(0, kValveCount); }

    std::optional<std::vector<bool>> decode_coils(const ModbusResult& r) const;   // 16 路电平
    std::optional<Valves> decode_open_states(const ModbusResult& r) const;        // 12 路「开」
    std::optional<std::vector<bool>> decode_inputs(const ModbusResult& r) const;  // 16 路 DI

    // ---- 同步便捷 ----
    std::optional<Valves> read_open_states(ModbusMaster& m) const { return decode_open_states(m.execute(req_read_coils())); }
    std::optional<std::vector<bool>> read_inputs(ModbusMaster& m) const { return decode_inputs(m.execute(req_read_inputs())); }
    bool set_valve(ModbusMaster& m, int coil_index, bool open) const { return m.execute(req_set_valve(coil_index, open)).ok; }
    bool write_valves(ModbusMaster& m, const Valves& open) const { return m.execute(req_write_valves(open)).ok; }
    bool close_range(ModbusMaster& m, int start, int count) const { return m.execute(req_close_range(start, count)).ok; }
    bool close_all(ModbusMaster& m) const { return m.execute(req_close_all_valves()).ok; }

private:
    ModbusRequest req_read_coils_raw(uint16_t start, uint16_t n) const { return ::reactor::hw::req_read_coils(slave_, start, n); }

    uint8_t slave_;
    bool energize_opens_;
};

}  // namespace reactor::hw
