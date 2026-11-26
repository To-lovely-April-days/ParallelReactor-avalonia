// 调试用追踪装饰器：把总线上每一笔事务（读/写、从站、地址、数据、耗时、失败原因）追加写入日志文件。
// C#: Hardware/TracingModbusMaster.cs（现场 run.sh 的 PR_STIR_TRACE=1 打开，用来排除「转速波动是不是软件下发的」）。
// 行格式照抄：「HH:MM:SS.mmm 【急】【写】多寄存器 从站1 0x6200×8 = [...] · 12ms」，紧急队列的事务带「【急】」。
// 写日志失败不影响事务。线程安全性同被包装的主站（文件写入自带锁）。
#pragma once
#include <memory>
#include <mutex>
#include <string>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

class TracingModbusMaster : public ModbusMaster {
public:
    TracingModbusMaster(std::shared_ptr<ModbusMaster> inner, std::string path);
    ModbusResult execute(const ModbusRequest& req) override;
    const std::string& path() const { return path_; }

private:
    void log_line(const std::string& line, bool urgent);

    std::shared_ptr<ModbusMaster> inner_;
    std::string path_;
    std::mutex mu_;
};

}  // namespace reactor::hw
