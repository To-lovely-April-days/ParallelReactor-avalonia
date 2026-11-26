#include "hw/rtu_master.hpp"

#include <cstdio>

#include "hw/modbus_rtu.hpp"

namespace reactor::hw {

RtuModbusMaster::RtuModbusMaster(SerialConfig cfg, int timeout_ms) : cfg_(std::move(cfg)), timeout_ms_(timeout_ms) {}

ModbusResult RtuModbusMaster::fail_close(std::string err) {
    port_.close();  // C#: catch { _sp.Close(); _sp = null; } —— 下笔事务重开
    return fail_result(std::move(err));
}

static std::string short_msg(size_t want, size_t got) {
    // C#: TimeoutException「Modbus 响应不足：期望 {n} 字节，实收 {got}」
    return "Modbus 响应不足：期望 " + std::to_string(want) + " 字节，实收 " + std::to_string(got);
}

ModbusResult RtuModbusMaster::execute(const ModbusRequest& req) {
    std::string bad = validate_request(req);
    if (!bad.empty()) return fail_result("请求无效：" + bad);  // 什么都没发，不用关串口

    if (!port_.is_open() && !port_.open(cfg_)) return fail_result(port_.last_error());

    const std::vector<uint8_t> frame = build_frame(req);
    port_.flush_input();
    if (!port_.write_all(frame)) return fail_close(port_.last_error());

    // 先读 地址 + 功能码，借此判断是否为异常响应
    std::vector<uint8_t> resp = port_.read_up_to(2, timeout_ms_);
    if (resp.size() < 2) return fail_close(short_msg(2, resp.size()));
    const uint8_t addr = resp[0], fn = resp[1];

    if (fn & 0x80) {
        std::vector<uint8_t> tail = port_.read_up_to(3, timeout_ms_);  // 异常码(1) + CRC(2)
        if (tail.size() < 3) return fail_close(short_msg(3, tail.size()));
        ModbusResult r = fail_close("");
        r.exception = tail[0];
        char b[128];
        std::snprintf(b, sizeof b, "Modbus 异常 0x%02X（%s）· 从站 %u 功能 0x%02X", tail[0], modbus_exception_text(tail[0]),
                      static_cast<unsigned>(addr), static_cast<unsigned>(fn & 0x7F));
        r.error = b;
        return r;
    }

    const size_t expected = expected_response_length(req.fc, req.count);
    std::vector<uint8_t> rest = port_.read_up_to(expected - 2, timeout_ms_);
    if (rest.size() < expected - 2) return fail_close(short_msg(expected - 2, rest.size()));
    resp.insert(resp.end(), rest.begin(), rest.end());

    if (addr != req.slave)
        return fail_close("Modbus 从站地址不匹配：期望 " + std::to_string(req.slave) + "，收到 " + std::to_string(addr));
    if (!crc_valid(resp)) return fail_close("Modbus CRC 校验失败");
    if (fn != req.fc) return fail_close("Modbus 功能码不匹配：期望 " + std::to_string(req.fc) + "，收到 " + std::to_string(fn));

    ModbusResult r;
    if (req.fc == kFcReadHolding || req.fc == kFcReadInput) {
        if (resp[2] != 2 * req.count) return fail_close("Modbus 字节数字段不对：" + std::to_string(resp[2]));
        r.regs.reserve(req.count);
        for (uint16_t i = 0; i < req.count; ++i)
            r.regs.push_back(static_cast<uint16_t>((resp[3 + 2 * i] << 8) | resp[4 + 2 * i]));
    } else if (req.fc == kFcReadCoils || req.fc == kFcReadDiscreteInputs) {
        if (resp[2] != expected - 5) return fail_close("Modbus 字节数字段不对：" + std::to_string(resp[2]));
        r.bits = decode_bits(resp, req.count);
    }
    r.ok = true;
    return r;
}

}  // namespace reactor::hw
