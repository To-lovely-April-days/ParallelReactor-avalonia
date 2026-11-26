#include "hw/tracing_master.hpp"

#include <cstdio>

#include "common/clock.hpp"
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

namespace reactor::hw {

TracingModbusMaster::TracingModbusMaster(std::shared_ptr<ModbusMaster> inner, std::string path)
    : inner_(std::move(inner)), path_(std::move(path)) {
    log_line("=== 追踪开始 " + local_fmt(wall_ms(), "%Y-%m-%d %H:%M:%S") + " ===", false);
}

void TracingModbusMaster::log_line(const std::string& line, bool urgent) {
    const int64_t now = wall_ms();
    char ms[8];
    std::snprintf(ms, sizeof ms, ".%03d", static_cast<int>(now % 1000));
    std::string out = local_fmt(now, "%H:%M:%S") + ms + " " + (urgent ? "【急】" : "") + line + "\n";
    std::lock_guard<std::mutex> lk(mu_);
    if (FILE* f = std::fopen(path_.c_str(), "a")) {
        std::fwrite(out.data(), 1, out.size(), f);
        std::fclose(f);
    }
}

ModbusResult TracingModbusMaster::execute(const ModbusRequest& req) {
    const int64_t t0 = mono_ms();
    ModbusResult r = inner_->execute(req);
    const int64_t dt = mono_ms() - t0;
    const std::string op = describe_request(req);
    if (r.ok) log_line(op + " · " + std::to_string(dt) + "ms", req.urgent);
    else log_line(op + " · 失败 " + r.error, req.urgent);
    return r;
}

}  // namespace reactor::hw
