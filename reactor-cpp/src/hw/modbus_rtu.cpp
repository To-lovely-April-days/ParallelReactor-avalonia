#include "hw/modbus_rtu.hpp"

#include <cstdio>

namespace reactor::hw {

namespace {
inline uint8_t hi(uint16_t v) { return static_cast<uint8_t>(v >> 8); }
inline uint8_t lo(uint16_t v) { return static_cast<uint8_t>(v & 0xFF); }
}  // namespace

uint16_t crc16(const uint8_t* data, size_t len) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < len; ++i) {
        crc ^= data[i];
        for (int b = 0; b < 8; ++b) {
            if (crc & 1) crc = static_cast<uint16_t>((crc >> 1) ^ 0xA001);
            else crc >>= 1;
        }
    }
    return crc;
}

void append_crc(std::vector<uint8_t>& f) {
    uint16_t c = crc16(f);
    f.push_back(lo(c));
    f.push_back(hi(c));
}

bool crc_valid(const uint8_t* f, size_t len) {
    if (len < 4) return false;
    uint16_t c = crc16(f, len - 2);
    return f[len - 2] == lo(c) && f[len - 1] == hi(c);
}

std::vector<uint8_t> build_read_request(uint8_t slave, uint8_t fc, uint16_t addr, uint16_t count) {
    std::vector<uint8_t> f{slave, fc, hi(addr), lo(addr), hi(count), lo(count)};
    append_crc(f);
    return f;
}

std::vector<uint8_t> build_write_single_coil(uint8_t slave, uint16_t addr, bool on) {
    std::vector<uint8_t> f{slave, 0x05, hi(addr), lo(addr), static_cast<uint8_t>(on ? 0xFF : 0x00), 0x00};
    append_crc(f);
    return f;
}

std::vector<uint8_t> build_write_single_register(uint8_t slave, uint16_t addr, uint16_t value) {
    std::vector<uint8_t> f{slave, 0x06, hi(addr), lo(addr), hi(value), lo(value)};
    append_crc(f);
    return f;
}

std::vector<uint8_t> build_write_multiple_coils(uint8_t slave, uint16_t addr, const std::vector<bool>& values) {
    const uint16_t n = static_cast<uint16_t>(values.size());
    const size_t nbytes = (values.size() + 7) / 8;
    std::vector<uint8_t> f{slave, 0x0F, hi(addr), lo(addr), hi(n), lo(n), static_cast<uint8_t>(nbytes)};
    f.resize(7 + nbytes, 0);
    for (size_t i = 0; i < values.size(); ++i)
        if (values[i]) f[7 + i / 8] = static_cast<uint8_t>(f[7 + i / 8] | (1u << (i % 8)));
    append_crc(f);
    return f;
}

std::vector<uint8_t> build_write_multiple_registers(uint8_t slave, uint16_t addr, const std::vector<uint16_t>& values) {
    const uint16_t n = static_cast<uint16_t>(values.size());
    std::vector<uint8_t> f{slave, 0x10, hi(addr), lo(addr), hi(n), lo(n), static_cast<uint8_t>(n * 2)};
    for (uint16_t v : values) { f.push_back(hi(v)); f.push_back(lo(v)); }
    append_crc(f);
    return f;
}

size_t expected_response_length(uint8_t fc, uint16_t count) {
    switch (fc) {
        case 0x01: case 0x02: return 5 + (static_cast<size_t>(count) + 7) / 8;
        case 0x03: case 0x04: return read_response_length(count);
        case 0x05: case 0x06: case 0x0F: case 0x10: return 8;
        default: return 0;
    }
}

const char* modbus_exception_text(uint8_t code) {
    switch (code) {
        case 0x01: return "非法功能码";
        case 0x02: return "非法数据地址";
        case 0x03: return "非法数据值";
        case 0x04: return "从站设备故障";
        case 0x05: return "确认（处理中）";
        case 0x06: return "从站忙";
        default: return "未知异常";
    }
}

static bool crc_ok(const std::vector<uint8_t>& f) { return crc_valid(f); }

ReadResult parse_read_response(const std::vector<uint8_t>& f, uint8_t slave, uint8_t fc, uint16_t count) {
    ReadResult r;
    if (f.size() < 5) { r.error = "回包太短（" + std::to_string(f.size()) + " 字节）"; return r; }
    if (f[0] != slave) { r.error = "站号不对：期望 " + std::to_string(slave) + "，收到 " + std::to_string(f[0]); return r; }
    if (f[1] == (fc | 0x80)) {
        if (!crc_ok(std::vector<uint8_t>(f.begin(), f.begin() + 5))) { r.error = "异常帧 CRC 错"; return r; }
        r.exception = f[2];
        char buf[64];
        std::snprintf(buf, sizeof buf, "从站回异常码 0x%02X", f[2]);
        r.error = buf;
        return r;
    }
    if (f[1] != fc) { r.error = "功能码不对：收到 " + std::to_string(f[1]); return r; }
    if (f.size() != read_response_length(count)) { r.error = "长度不对：收到 " + std::to_string(f.size()) + " 字节"; return r; }
    if (f[2] != 2 * count) { r.error = "字节数字段不对：" + std::to_string(f[2]); return r; }
    if (!crc_ok(f)) { r.error = "CRC 校验失败"; return r; }
    r.regs.reserve(count);
    for (uint16_t i = 0; i < count; ++i)
        r.regs.push_back(static_cast<uint16_t>((f[3 + 2 * i] << 8) | f[4 + 2 * i]));
    r.ok = true;
    return r;
}

std::vector<bool> decode_bits(const std::vector<uint8_t>& f, uint16_t count) {
    std::vector<bool> bits(count, false);
    for (uint16_t i = 0; i < count; ++i) {
        size_t idx = 3 + i / 8;
        if (idx < f.size()) bits[i] = (f[idx] & (1u << (i % 8))) != 0;
    }
    return bits;
}

}  // namespace reactor::hw
