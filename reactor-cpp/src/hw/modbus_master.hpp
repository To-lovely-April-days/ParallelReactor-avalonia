// Modbus 主站抽象：一笔「事务」= 一问一答。C#: Hardware/IModbusMaster.cs
//
// 和 C# 的差别：C# 是 async Task + 异常；这里 execute() 是**同步阻塞**的，结果放在 ModbusResult 里（不抛异常）。
// 真串口实现只能由它所属总线的 I/O 线程调用（见 bus.hpp）；排队、优先级、异步回调都在 Bus 里做。
//
// 线程：
//   - ModbusMaster 接口本身不保证线程安全，看具体实现：
//     RtuModbusMaster 只能一个线程用（总线线程）；MockModbusMaster / OfflineModbusMaster 线程安全；
//     TracingModbusMaster 的线程安全性等于被包装的主站。
//   - 本文件里的 req_* / validate_request / describe_request 都是纯函数。
#pragma once
#include <cmath>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace reactor::hw {

// 功能码
constexpr uint8_t kFcReadCoils = 0x01;
constexpr uint8_t kFcReadDiscreteInputs = 0x02;
constexpr uint8_t kFcReadHolding = 0x03;
constexpr uint8_t kFcReadInput = 0x04;
constexpr uint8_t kFcWriteCoil = 0x05;
constexpr uint8_t kFcWriteRegister = 0x06;
constexpr uint8_t kFcWriteCoils = 0x0F;
constexpr uint8_t kFcWriteRegisters = 0x10;

// 一笔事务的请求。用 req_* 构造，不要手填。
struct ModbusRequest {
    uint8_t slave = 1;
    uint8_t fc = 0;
    uint16_t addr = 0;
    uint16_t count = 0;            // 读：个数；写：值的个数（单写为 1）
    std::vector<uint16_t> regs;    // 0x06（1 个）/ 0x10
    std::vector<bool> bits;        // 0x05（1 个）/ 0x0F
    bool urgent = false;           // 由 Bus 按所走的队列填写（追踪日志用），调用方不用管

    bool is_read() const { return fc >= kFcReadCoils && fc <= kFcReadInput; }
    bool is_write() const { return !is_read(); }
};

// 一笔事务的结果。ok=false 时 error 是中文原因；从站回异常码时 exception 非 0。
struct ModbusResult {
    bool ok = false;
    uint8_t exception = 0;
    std::string error;
    std::vector<uint16_t> regs;    // 0x03/0x04 成功时
    std::vector<bool> bits;        // 0x01/0x02 成功时
    int64_t elapsed_ms = 0;        // 由 Bus 填写：在总线上实际耗时（不含排队）
};

ModbusResult fail_result(std::string error);

ModbusRequest req_read_coils(uint8_t slave, uint16_t addr, uint16_t count);
ModbusRequest req_read_discrete_inputs(uint8_t slave, uint16_t addr, uint16_t count);
ModbusRequest req_read_holding(uint8_t slave, uint16_t addr, uint16_t count);
ModbusRequest req_read_input(uint8_t slave, uint16_t addr, uint16_t count);
ModbusRequest req_write_coil(uint8_t slave, uint16_t addr, bool on);
ModbusRequest req_write_register(uint8_t slave, uint16_t addr, uint16_t value);
ModbusRequest req_write_coils(uint8_t slave, uint16_t addr, std::vector<bool> values);
ModbusRequest req_write_registers(uint8_t slave, uint16_t addr, std::vector<uint16_t> values);

// 检查请求是否合法（站号 1..247、个数在协议上限内、值个数与 count 一致）。合法返回空串。
std::string validate_request(const ModbusRequest& r);
// 请求编成 RTU 帧（含 CRC）。调用前先 validate_request。
std::vector<uint8_t> build_frame(const ModbusRequest& r);
// 给日志看的一行描述，照 C# TracingModbusMaster 的写法：「读寄存器 从站1 0x0600×8」「【写】寄存器 从站1 0x0300 = 0x0004」
std::string describe_request(const ModbusRequest& r);

// ---- 数值换算小工具（驱动共用） ----
// C# Math.Round(double) 默认「四舍六入五成双」；std::nearbyint 在默认舍入模式下也是五成双。非有限值按 0。
inline int64_t round_even(double x) { return std::isfinite(x) ? static_cast<int64_t>(std::nearbyint(x)) : 0; }
// C# (ushort)(short)Math.Round(x)：按有符号 16 位写寄存器。C# 越界是未定义的截断，这里夹到 int16 范围。
inline uint16_t reg_from_i16(double x) {
    int64_t v = round_even(x);
    if (v < -32768) v = -32768;
    if (v > 32767) v = 32767;
    return static_cast<uint16_t>(static_cast<int16_t>(v));
}
// C# (ushort)Math.Round(x)：按无符号 16 位写寄存器，夹到 0..65535。
inline uint16_t reg_from_u16(double x) {
    int64_t v = round_even(x);
    if (v < 0) v = 0;
    if (v > 65535) v = 65535;
    return static_cast<uint16_t>(v);
}

class ModbusMaster {
public:
    virtual ~ModbusMaster() = default;
    // 做一笔完整事务（阻塞到收完回包或超时）。永不抛异常。
    virtual ModbusResult execute(const ModbusRequest& req) = 0;

    // 便捷封装（照 IModbusMaster 的 8 个功能码）
    ModbusResult read_coils(uint8_t s, uint16_t a, uint16_t n) { return execute(req_read_coils(s, a, n)); }
    ModbusResult read_discrete_inputs(uint8_t s, uint16_t a, uint16_t n) { return execute(req_read_discrete_inputs(s, a, n)); }
    ModbusResult read_holding(uint8_t s, uint16_t a, uint16_t n) { return execute(req_read_holding(s, a, n)); }
    ModbusResult read_input(uint8_t s, uint16_t a, uint16_t n) { return execute(req_read_input(s, a, n)); }
    ModbusResult write_coil(uint8_t s, uint16_t a, bool on) { return execute(req_write_coil(s, a, on)); }
    ModbusResult write_register(uint8_t s, uint16_t a, uint16_t v) { return execute(req_write_register(s, a, v)); }
    ModbusResult write_coils(uint8_t s, uint16_t a, std::vector<bool> v) { return execute(req_write_coils(s, a, std::move(v))); }
    ModbusResult write_registers(uint8_t s, uint16_t a, std::vector<uint16_t> v) { return execute(req_write_registers(s, a, std::move(v))); }
};

// 在一个主站上依次做一串事务；stop_on_fail 时遇到失败即停（结果只含已做的部分）。
std::vector<ModbusResult> execute_seq(ModbusMaster& m, const std::vector<ModbusRequest>& reqs, bool stop_on_fail = true);

// 端口未配置时用的主站：每笔事务都失败、不产生数据。上层按「通讯失败」处理（离线、读数「—」、气路拒绝启动）。
// C#: Hardware/OfflineModbusMaster.cs。线程安全。
class OfflineModbusMaster : public ModbusMaster {
public:
    // what：模块说明，如「温控器 AI-8（hardware.json temp.port）」，错误信息 = what + 「未配置串口」。
    explicit OfflineModbusMaster(std::string what) : what_(std::move(what)) {}
    ModbusResult execute(const ModbusRequest&) override { return fail_result(what_ + "未配置串口"); }

private:
    std::string what_;
};

}  // namespace reactor::hw
