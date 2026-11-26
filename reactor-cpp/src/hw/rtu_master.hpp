// 走真实串口（RS485）的 Modbus RTU 主站。C#: Hardware/ModbusRtuMaster.cs（TxnCore 部分）
//
// 每笔事务：串口惰性打开 → 丢弃输入缓冲 → 写帧 → 先读 2 字节（地址 + 功能码）判异常 →
// 异常帧再读 3 字节；正常帧读剩余字节 → 校验地址、功能码、CRC。**不自动重试**。
// 任何错误（打不开、写失败、超时、异常码、地址/CRC 错）都关闭串口，下一笔事务重开（清掉可能错位的半截帧）。
// 超时：每一段读（头 2 字节、剩余部分）各自最多等 timeout_ms（默认 300 ms，同 C# ReadTimeout）。
//
// 线程：不是线程安全的。只能由所属总线的 I/O 线程调用（Bus 保证串行）。
#pragma once
#include <string>

#include "hw/modbus_master.hpp"
#include "hw/serial_port.hpp"

namespace reactor::hw {

class RtuModbusMaster : public ModbusMaster {
public:
    explicit RtuModbusMaster(SerialConfig cfg, int timeout_ms = 300);
    ModbusResult execute(const ModbusRequest& req) override;

    const SerialConfig& config() const { return cfg_; }
    bool is_open() const { return port_.is_open(); }
    int timeout_ms() const { return timeout_ms_; }

private:
    ModbusResult fail_close(std::string err);

    SerialConfig cfg_;
    int timeout_ms_;
    SerialPort port_;
};

}  // namespace reactor::hw
