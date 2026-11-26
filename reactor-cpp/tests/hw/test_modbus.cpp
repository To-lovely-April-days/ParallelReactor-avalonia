// Modbus CRC、组帧、解析、请求校验
#include "hw/modbus_master.hpp"
#include "hw/modbus_rtu.hpp"
#include "testing.hpp"

using namespace reactor::hw;

TEST(hw_modbus, frames) {
    // 标准例子：01 03 00 00 00 0A → CRC C5 CD
    auto req = build_read_request(1, 3, 0x0000, 10);
    CHECK(req.size() == 8);
    CHECK(req[6] == 0xC5 && req[7] == 0xCD);

    // 温控 PV 读请求：01 03 06 00 00 08
    auto t = build_read_request(1, 3, 0x0600, 8);
    CHECK(t[2] == 0x06 && t[3] == 0x00 && t[5] == 0x08);
    CHECK(crc16(t) == 0);  // 带 CRC 的整帧再算一遍 CRC 应为 0

    // 正常响应：读 2 个寄存器 = 0x00FA(25.0°C) 和 0xFFF6(-1.0°C)
    std::vector<uint8_t> ok{0x01, 0x03, 0x04, 0x00, 0xFA, 0xFF, 0xF6};
    uint16_t c = crc16(ok);
    ok.push_back(c & 0xFF);
    ok.push_back(c >> 8);
    auto r = parse_read_response(ok, 1, 3, 2);
    CHECK(r.ok);
    CHECK(r.regs.size() == 2 && r.regs[0] == 0x00FA && r.regs[1] == 0xFFF6);
    CHECK(static_cast<int16_t>(r.regs[1]) == -10);

    // CRC 错
    auto bad = ok;
    bad[4] ^= 0x01;
    CHECK(!parse_read_response(bad, 1, 3, 2).ok);

    // 站号不对
    CHECK(!parse_read_response(ok, 2, 3, 2).ok);

    // 异常帧：01 83 02 + CRC
    std::vector<uint8_t> ex{0x01, 0x83, 0x02};
    c = crc16(ex);
    ex.push_back(c & 0xFF);
    ex.push_back(c >> 8);
    auto re = parse_read_response(ex, 1, 3, 2);
    CHECK(!re.ok && re.exception == 0x02);

    // 长度不对
    std::vector<uint8_t> shortf(ok.begin(), ok.end() - 1);
    CHECK(!parse_read_response(shortf, 1, 3, 2).ok);
}

TEST(hw_modbus, write_frames) {
    // 0x05 写单线圈 ON：01 05 00 0B FF 00 + CRC
    auto f5 = build_write_single_coil(1, 0x000B, true);
    CHECK(f5.size() == 8);
    CHECK(f5[1] == 0x05 && f5[3] == 0x0B && f5[4] == 0xFF && f5[5] == 0x00);
    CHECK(crc_valid(f5));
    CHECK(build_write_single_coil(1, 0, false)[4] == 0x00);

    // 0x06 写 At=4：01 06 03 00 00 04
    auto f6 = build_write_single_register(1, 0x0300, 4);
    CHECK(f6[1] == 0x06 && f6[2] == 0x03 && f6[3] == 0x00 && f6[4] == 0x00 && f6[5] == 0x04);
    CHECK(crc_valid(f6));

    // 0x0F 写 12 路线圈：SV2(1)、气体A(9)、排空(11) → 字节 0x02、0x0A
    std::vector<bool> coils(12, false);
    coils[1] = coils[9] = coils[11] = true;
    auto ff = build_write_multiple_coils(1, 0, coils);
    CHECK(ff.size() == 7 + 2 + 2);
    CHECK(ff[1] == 0x0F && ff[5] == 12 && ff[6] == 2);
    CHECK(ff[7] == 0x02 && ff[8] == 0x0A);
    CHECK(crc_valid(ff));

    // 0x10 写 DM2C 速度路径 8 个寄存器
    auto f10 = build_write_multiple_registers(1, 0x6200, {2, 0, 0, 895, 1000, 1000, 0, 0x10});
    CHECK(f10.size() == 7 + 16 + 2);
    CHECK(f10[1] == 0x10 && f10[2] == 0x62 && f10[3] == 0x00 && f10[5] == 8 && f10[6] == 16);
    CHECK(f10[13] == 0x03 && f10[14] == 0x7F);  // 895 = 0x037F
    CHECK(crc_valid(f10));

    CHECK_EQ(expected_response_length(0x01, 16), static_cast<size_t>(7));
    CHECK_EQ(expected_response_length(0x02, 9), static_cast<size_t>(7));
    CHECK_EQ(expected_response_length(0x03, 8), static_cast<size_t>(21));
    CHECK_EQ(expected_response_length(0x0F, 12), static_cast<size_t>(8));
    CHECK_EQ(expected_response_length(0x10, 8), static_cast<size_t>(8));
    CHECK_EQ(expected_response_length(0x2B, 1), static_cast<size_t>(0));

    // 位解码：01 01 02 05 80 → 位 0、2、15
    std::vector<uint8_t> rb{0x01, 0x01, 0x02, 0x05, 0x80};
    append_crc(rb);
    auto bits = decode_bits(rb, 16);
    CHECK(bits[0] && !bits[1] && bits[2] && bits[15] && !bits[14]);
    CHECK(std::string(modbus_exception_text(0x02)) == "非法数据地址");
}

TEST(hw_modbus, request_validate_describe) {
    CHECK(validate_request(req_read_holding(1, 0, 8)).empty());
    CHECK(!validate_request(req_read_holding(0, 0, 8)).empty());    // 站号 0（广播）不允许
    CHECK(!validate_request(req_read_holding(248, 0, 8)).empty());
    CHECK(!validate_request(req_read_holding(1, 0, 0)).empty());
    CHECK(!validate_request(req_read_holding(1, 0, 126)).empty());
    CHECK(validate_request(req_read_coils(1, 0, 2000)).empty());
    CHECK(!validate_request(req_write_registers(1, 0, std::vector<uint16_t>(124, 0))).empty());
    CHECK(validate_request(req_write_coils(1, 0, std::vector<bool>(12, false))).empty());
    ModbusRequest bogus = req_read_holding(1, 0, 1);
    bogus.fc = 0x2B;
    CHECK(!validate_request(bogus).empty());

    // 追踪日志描述（照 C# TracingModbusMaster）
    CHECK_EQ(describe_request(req_read_holding(1, 0x0600, 8)), std::string("读寄存器 从站1 0x0600×8"));
    CHECK_EQ(describe_request(req_write_register(1, 0x0300, 4)), std::string("【写】寄存器 从站1 0x0300 = 0x0004"));
    CHECK_EQ(describe_request(req_write_coil(1, 3, true)), std::string("【写】线圈 从站1 0x0003 = ON"));
    CHECK_EQ(describe_request(req_write_coils(1, 0, std::vector<bool>(12))), std::string("【写】多线圈 从站1 0x0000×12"));
    CHECK_EQ(describe_request(req_write_registers(1, 0x6200, {2, 0x10})),
             std::string("【写】多寄存器 从站1 0x6200×2 = [0x0002,0x0010]"));

    // build_frame 与专用组帧一致
    CHECK(build_frame(req_write_register(1, 0x0300, 4)) == build_write_single_register(1, 0x0300, 4));
    CHECK(build_frame(req_read_input(1, 0, 8)) == build_read_request(1, 4, 0, 8));
}

TEST(hw_modbus, rounding_helpers) {
    // C# Math.Round 是五成双
    CHECK_EQ(round_even(122.5), static_cast<int64_t>(122));
    CHECK_EQ(round_even(123.5), static_cast<int64_t>(124));
    CHECK_EQ(round_even(-2.5), static_cast<int64_t>(-2));
    CHECK_EQ(round_even(std::nan("")), static_cast<int64_t>(0));
    CHECK_EQ(reg_from_i16(-105.0), static_cast<uint16_t>(0xFF97));
    CHECK_EQ(reg_from_i16(40000.0), static_cast<uint16_t>(32767));
    CHECK_EQ(reg_from_u16(-3.0), static_cast<uint16_t>(0));
    CHECK_EQ(reg_from_u16(70000.0), static_cast<uint16_t>(65535));

    OfflineModbusMaster off("温控器 AI-8（hardware.json temp.port）");
    auto r = off.read_holding(1, 0, 1);
    CHECK(!r.ok);
    CHECK_EQ(r.error, std::string("温控器 AI-8（hardware.json temp.port）未配置串口"));
}
