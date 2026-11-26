#include "hw/modbus_master.hpp"

#include <cstdio>

#include "hw/modbus_rtu.hpp"
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hw {

ModbusResult fail_result(std::string error) {
    ModbusResult r;
    r.ok = false;
    r.error = std::move(error);
    return r;
}

static ModbusRequest make_read(uint8_t slave, uint8_t fc, uint16_t addr, uint16_t count) {
    ModbusRequest r;
    r.slave = slave;
    r.fc = fc;
    r.addr = addr;
    r.count = count;
    return r;
}

ModbusRequest req_read_coils(uint8_t s, uint16_t a, uint16_t n) { return make_read(s, kFcReadCoils, a, n); }
ModbusRequest req_read_discrete_inputs(uint8_t s, uint16_t a, uint16_t n) { return make_read(s, kFcReadDiscreteInputs, a, n); }
ModbusRequest req_read_holding(uint8_t s, uint16_t a, uint16_t n) { return make_read(s, kFcReadHolding, a, n); }
ModbusRequest req_read_input(uint8_t s, uint16_t a, uint16_t n) { return make_read(s, kFcReadInput, a, n); }

ModbusRequest req_write_coil(uint8_t s, uint16_t a, bool on) {
    ModbusRequest r = make_read(s, kFcWriteCoil, a, 1);
    r.bits = {on};
    return r;
}

ModbusRequest req_write_register(uint8_t s, uint16_t a, uint16_t v) {
    ModbusRequest r = make_read(s, kFcWriteRegister, a, 1);
    r.regs = {v};
    return r;
}

ModbusRequest req_write_coils(uint8_t s, uint16_t a, std::vector<bool> values) {
    ModbusRequest r = make_read(s, kFcWriteCoils, a, static_cast<uint16_t>(values.size()));
    r.bits = std::move(values);
    return r;
}

ModbusRequest req_write_registers(uint8_t s, uint16_t a, std::vector<uint16_t> values) {
    ModbusRequest r = make_read(s, kFcWriteRegisters, a, static_cast<uint16_t>(values.size()));
    r.regs = std::move(values);
    return r;
}

std::string validate_request(const ModbusRequest& r) {
    if (r.slave < 1 || r.slave > 247) return "站号无效：" + std::to_string(r.slave);
    auto range = [&](unsigned lo, unsigned hi) -> std::string {
        if (r.count < lo || r.count > hi)
            return "个数无效：" + std::to_string(r.count) + "（应为 " + std::to_string(lo) + "–" + std::to_string(hi) + "）";
        return {};
    };
    switch (r.fc) {
        case kFcReadCoils:
        case kFcReadDiscreteInputs: return range(1, 2000);
        case kFcReadHolding:
        case kFcReadInput: return range(1, 125);
        case kFcWriteCoil:
            if (r.bits.size() != 1) return "写单个线圈需要 1 个值";
            return {};
        case kFcWriteRegister:
            if (r.regs.size() != 1) return "写单个寄存器需要 1 个值";
            return {};
        case kFcWriteCoils:
            if (r.bits.size() != r.count) return "线圈值个数与 count 不一致";
            return range(1, 1968);
        case kFcWriteRegisters:
            if (r.regs.size() != r.count) return "寄存器值个数与 count 不一致";
            return range(1, 123);
        default: {
            char b[48];
            std::snprintf(b, sizeof b, "不支持的功能码 0x%02X", r.fc);
            return b;
        }
    }
}

std::vector<uint8_t> build_frame(const ModbusRequest& r) {
    switch (r.fc) {
        case kFcWriteCoil: return build_write_single_coil(r.slave, r.addr, !r.bits.empty() && r.bits[0]);
        case kFcWriteRegister: return build_write_single_register(r.slave, r.addr, r.regs.empty() ? 0 : r.regs[0]);
        case kFcWriteCoils: return build_write_multiple_coils(r.slave, r.addr, r.bits);
        case kFcWriteRegisters: return build_write_multiple_registers(r.slave, r.addr, r.regs);
        default: return build_read_request(r.slave, r.fc, r.addr, r.count);
    }
}

std::vector<ModbusResult> execute_seq(ModbusMaster& m, const std::vector<ModbusRequest>& reqs, bool stop_on_fail) {
    std::vector<ModbusResult> out;
    for (const auto& q : reqs) {
        out.push_back(m.execute(q));
        if (stop_on_fail && !out.back().ok) break;
    }
    return out;
}

static std::string hex4(uint16_t v) {
    char b[8];
    std::snprintf(b, sizeof b, "0x%04X", v);
    return b;
}

std::string describe_request(const ModbusRequest& r) {
    const std::string s = "从站" + std::to_string(r.slave) + " ";
    const std::string n = "×" + std::to_string(r.count);
    switch (r.fc) {
        case kFcReadCoils: return "读线圈 " + s + hex4(r.addr) + n;
        case kFcReadDiscreteInputs: return "读离散输入 " + s + hex4(r.addr) + n;
        case kFcReadHolding: return "读寄存器 " + s + hex4(r.addr) + n;
        case kFcReadInput: return "读输入寄存器 " + s + hex4(r.addr) + n;
        case kFcWriteCoil:
            return "【写】线圈 " + s + hex4(r.addr) + " = " + (!r.bits.empty() && r.bits[0] ? "ON" : "OFF");
        case kFcWriteRegister: return "【写】寄存器 " + s + hex4(r.addr) + " = " + hex4(r.regs.empty() ? 0 : r.regs[0]);
        case kFcWriteCoils: return "【写】多线圈 " + s + hex4(r.addr) + n;
        case kFcWriteRegisters: {
            std::string v;
            for (size_t i = 0; i < r.regs.size(); ++i) { if (i) v += ","; v += hex4(r.regs[i]); }
            return "【写】多寄存器 " + s + hex4(r.addr) + n + " = [" + v + "]";
        }
        default: return "功能码 " + std::to_string(r.fc) + " " + s + hex4(r.addr);
    }
}

}  // namespace reactor::hw
