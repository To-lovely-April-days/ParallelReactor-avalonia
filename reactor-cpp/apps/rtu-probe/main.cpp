// rtu-probe：M0 用的串口 / Modbus 只读探测工具。只发「读寄存器」请求，不写任何东西。
//
// 用法：
//   rtu-probe --preset temp            # 温控 AI-8：/dev/ttyS5 19200，读 8 路 PV
//   rtu-probe --preset press           # 压力 8AI：/dev/ttyS6 9600，读 8 路原码并换算 psi
//   rtu-probe --preset stir            # 搅拌 DM2C：/dev/ttyS4 38400，读状态字
//   rtu-probe --port /dev/ttyS5 --baud 9600 --slave 1 --fc 3 --addr 0x0600 --count 8
//   任何参数都能覆盖预设，比如 --preset temp --baud 9600
//   --repeat N  连读 N 次（每次间隔 1 s），--timeout 毫秒（默认 300）
#include "hw/modbus_rtu.hpp"
#include "hw/serial_port.hpp"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>

using namespace reactor::hw;

namespace {

struct Opts {
    std::string preset;
    SerialConfig ser{"", 9600, 'N', 8, 1};
    int slave = 1, fc = 3, addr = 0, count = 1, repeat = 1, timeout = 300;
};

void usage() {
    std::puts("rtu-probe --preset temp|press|stir  [--port P] [--baud B] [--slave S] [--fc 3|4]\n"
              "          [--addr A] [--count N] [--repeat N] [--timeout MS]");
}

bool parse_int(const char* s, int& out) {
    char* end = nullptr;
    long v = std::strtol(s, &end, 0);  // 支持 0x 前缀
    if (!s[0] || *end) return false;
    out = static_cast<int>(v);
    return true;
}

void apply_preset(Opts& o) {
    // 端口和站号照现场 run.sh；温控 / 搅拌的波特率是现在程序实际在用的默认值（run.sh 没设）
    if (o.preset == "temp") { o.ser.path = "/dev/ttyS5"; o.ser.baud = 19200; o.fc = 3; o.addr = 0x0600; o.count = 8; }
    else if (o.preset == "press") { o.ser.path = "/dev/ttyS6"; o.ser.baud = 9600; o.fc = 4; o.addr = 0x0000; o.count = 8; }
    else if (o.preset == "stir") { o.ser.path = "/dev/ttyS4"; o.ser.baud = 38400; o.fc = 3; o.addr = 0x1003; o.count = 1; }
}

void print_values(const Opts& o, const ReadResult& r) {
    for (size_t i = 0; i < r.regs.size(); ++i) {
        uint16_t raw = r.regs[i];
        if (o.preset == "temp") {
            double pv = static_cast<int16_t>(raw) / 10.0;  // dPt=1
            std::printf("  %zu 号  PV = %6.1f °C   (原码 %u)\n", i + 1, pv, raw);
        } else if (o.preset == "press") {
            // 4-20 mA：原码 3200..16000 对应 0..600 psi
            double psi = (static_cast<double>(raw) - 3200.0) / (16000.0 - 3200.0) * 600.0;
            std::printf("  %zu 号  %7.1f psi  %6.3f MPa   (原码 %u)\n", i + 1, psi, psi * 0.00689476, raw);
        } else {
            std::printf("  [0x%04X] = %u (0x%04X)\n", o.addr + static_cast<int>(i), raw, raw);
        }
    }
}

}  // namespace

int main(int argc, char** argv) {
    Opts o;
    // 先找预设，再让其余参数覆盖
    for (int i = 1; i < argc - 1; ++i)
        if (!std::strcmp(argv[i], "--preset")) { o.preset = argv[i + 1]; apply_preset(o); }
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "-h" || a == "--help") { usage(); return 0; }
        if (i + 1 >= argc) { usage(); return 2; }
        const char* v = argv[++i];
        bool ok = true;
        if (a == "--preset") continue;
        else if (a == "--port") o.ser.path = v;
        else if (a == "--baud") ok = parse_int(v, o.ser.baud);
        else if (a == "--slave") ok = parse_int(v, o.slave);
        else if (a == "--fc") ok = parse_int(v, o.fc);
        else if (a == "--addr") ok = parse_int(v, o.addr);
        else if (a == "--count") ok = parse_int(v, o.count);
        else if (a == "--repeat") ok = parse_int(v, o.repeat);
        else if (a == "--timeout") ok = parse_int(v, o.timeout);
        else { std::fprintf(stderr, "不认识的参数 %s\n", a.c_str()); usage(); return 2; }
        if (!ok) { std::fprintf(stderr, "参数 %s 的值不对：%s\n", a.c_str(), v); return 2; }
    }
    if (o.ser.path.empty()) { usage(); return 2; }
    if (o.fc != 3 && o.fc != 4) { std::fprintf(stderr, "只支持功能码 3 / 4（只读）\n"); return 2; }
    if (o.count < 1 || o.count > 125) { std::fprintf(stderr, "count 要在 1..125\n"); return 2; }

    SerialPort port;
    if (!port.open(o.ser)) { std::fprintf(stderr, "%s\n", port.last_error().c_str()); return 1; }
    std::printf("%s  %d 8%c%d  站号 %d  功能码 %d  地址 0x%04X  数量 %d\n", o.ser.path.c_str(), o.ser.baud,
                o.ser.parity, o.ser.stop_bits, o.slave, o.fc, o.addr, o.count);

    int fails = 0;
    for (int n = 0; n < o.repeat; ++n) {
        if (n) std::this_thread::sleep_for(std::chrono::seconds(1));
        auto req = build_read_request(static_cast<uint8_t>(o.slave), static_cast<uint8_t>(o.fc),
                                      static_cast<uint16_t>(o.addr), static_cast<uint16_t>(o.count));
        port.flush_input();
        auto t0 = std::chrono::steady_clock::now();
        if (!port.write_all(req)) { std::fprintf(stderr, "%s\n", port.last_error().c_str()); return 1; }
        auto resp = port.read_up_to(read_response_length(static_cast<uint16_t>(o.count)), o.timeout);
        auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - t0).count();
        if (resp.empty()) { std::printf("第 %d 次：%d ms 内没有回包（检查接线、波特率、站号）\n", n + 1, o.timeout); ++fails; continue; }
        auto r = parse_read_response(resp, static_cast<uint8_t>(o.slave), static_cast<uint8_t>(o.fc), static_cast<uint16_t>(o.count));
        if (!r.ok) {
            std::printf("第 %d 次：%s（收到 %zu 字节：", n + 1, r.error.c_str(), resp.size());
            for (auto b : resp) std::printf("%02X ", b);
            std::puts("）");
            ++fails;
            continue;
        }
        std::printf("第 %d 次：OK，%lld ms\n", n + 1, static_cast<long long>(ms));
        print_values(o, r);
    }
    return fails == o.repeat ? 1 : 0;
}
