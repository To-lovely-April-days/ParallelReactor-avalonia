// Modbus RTU 帧：CRC、组帧（FC 01/02/03/04/05/06/0F/10）、响应解析。纯函数，不碰串口，便于单测。
// 地址一律是 Modbus 协议地址（0 基），与各模块手册一致。线程安全（无状态）。
#pragma once
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hw {

// Modbus CRC16（多项式 0xA001，初值 0xFFFF）。结果低字节在前发送。
uint16_t crc16(const uint8_t* data, size_t len);
inline uint16_t crc16(const std::vector<uint8_t>& v) { return crc16(v.data(), v.size()); }

// 在帧末尾追加 CRC（低字节在前）。
void append_crc(std::vector<uint8_t>& frame);
// 校验一帧（最后两字节为 CRC）。长度 < 4 一律 false。C#: Hardware/ModbusCrc.cs Validate
bool crc_valid(const uint8_t* frame, size_t len);
inline bool crc_valid(const std::vector<uint8_t>& f) { return crc_valid(f.data(), f.size()); }

// 组一帧「读」请求：功能码 0x01 / 0x02 / 0x03 / 0x04（带 CRC）。
std::vector<uint8_t> build_read_request(uint8_t slave, uint8_t fc, uint16_t addr, uint16_t count);
// 0x05 写单个线圈（FF00 = 闭合，0000 = 断开）
std::vector<uint8_t> build_write_single_coil(uint8_t slave, uint16_t addr, bool on);
// 0x06 写单个寄存器
std::vector<uint8_t> build_write_single_register(uint8_t slave, uint16_t addr, uint16_t value);
// 0x0F 写多个线圈（第 i 个值放在第 i/8 字节的第 i%8 位）
std::vector<uint8_t> build_write_multiple_coils(uint8_t slave, uint16_t addr, const std::vector<bool>& values);
// 0x10 写多个寄存器（大端）
std::vector<uint8_t> build_write_multiple_registers(uint8_t slave, uint16_t addr, const std::vector<uint16_t>& values);

// 读寄存器（0x03/0x04）响应的期望总长度（含地址、功能码、字节数、数据、CRC）。
inline size_t read_response_length(uint16_t count) { return 5 + 2u * count; }
// 任一功能码正常响应的期望总长度：01/02 → 5+ceil(n/8)；03/04 → 5+2n；05/06/0F/10 → 8。未知功能码返回 0。
size_t expected_response_length(uint8_t fc, uint16_t count);

// 异常码的中文说明。C#: Hardware/ModbusRtuMaster.cs ModbusException.Describe
const char* modbus_exception_text(uint8_t code);

struct ReadResult {
    bool ok = false;
    uint8_t exception = 0;          // 从站回了异常码时非 0
    std::string error;              // 出错原因（中文，给日志和界面）
    std::vector<uint16_t> regs;     // 成功时的寄存器值
};

// 解析读寄存器响应（0x03/0x04）。frame 是收到的完整一帧（异常帧 5 字节，正常帧 5+2N 字节）。
ReadResult parse_read_response(const std::vector<uint8_t>& frame, uint8_t slave, uint8_t fc, uint16_t count);

// 从 0x01/0x02 的完整响应帧里取 count 个位（不校验，调用方先校验长度和 CRC）。
std::vector<bool> decode_bits(const std::vector<uint8_t>& frame, uint16_t count);

}  // namespace reactor::hw
