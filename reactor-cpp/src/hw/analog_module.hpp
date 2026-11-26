// 艾莫迅 JY-MODBUS-8AI：8 路模拟量输入（0x04 读输入寄存器 0x0000..0x0007）。C#: Hardware/AnalogModule.cs
// 原码 0..16000：0-10V / 0-20mA 对应 0..16000；4-20mA 对应 3200..16000。本系统接 8 路压力变送器（psi，表压）。
// 满量程默认 600 psi（C#: HardwareOptions.PressFullScale，PR_PRESS_FULLSCALE）。
// 对象不可变，任何线程可用。
#pragma once
#include <cstdint>
#include <optional>
#include <vector>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

enum class PressSignal { Current4to20mA, Voltage0to10V };

class AnalogModule {
public:
    static constexpr int kChannels = 8;
    static constexpr double kRawLo4to20 = 3200.0;
    static constexpr double kRawHi = 16000.0;
    static constexpr double kDefaultFullScalePsi = 600.0;

    explicit AnalogModule(uint8_t slave = 1, int count = kChannels) : slave_(slave), count_(count) {}
    uint8_t slave() const { return slave_; }
    int count() const { return count_; }

    // 原码线性换算为工程量。is4to20 时起点 3200（4 mA），否则 0。低于起点按 0；**高于 16000 不夹**（同 C#）。
    static double scale(uint16_t raw, double eng_lo, double eng_hi, bool is4to20);
    // 原码 → psi（0..fullscale）
    static double to_psi(uint16_t raw, double fullscale_psi, PressSignal sig) {
        return scale(raw, 0.0, fullscale_psi, sig == PressSignal::Current4to20mA);
    }

    ModbusRequest req_read_raw() const { return req_read_input(slave_, 0x0000, static_cast<uint16_t>(count_)); }
    std::optional<std::vector<uint16_t>> decode_raw(const ModbusResult& r) const;
    std::optional<std::vector<double>> decode_psi(const ModbusResult& r, double fullscale_psi, PressSignal sig) const;

    std::optional<std::vector<uint16_t>> read_raw(ModbusMaster& m) const { return decode_raw(m.execute(req_read_raw())); }
    std::optional<std::vector<double>> read_scaled(ModbusMaster& m, double eng_lo, double eng_hi, bool is4to20) const;

private:
    uint8_t slave_;
    int count_;
};

}  // namespace reactor::hw
