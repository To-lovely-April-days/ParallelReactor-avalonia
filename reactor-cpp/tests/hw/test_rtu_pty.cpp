// RTU 主站对着伪终端（pty）上的假从站跑真实的串口收发：正常读写、异常码、CRC 错、站号错、超时、
// 丢弃残留输入、出错后关串口下次重开；再接一条 Bus + AI-8 驱动跑一轮轮询。
// 用 posix_openpt（macOS / Linux 都在 libc 里，不需要 -lutil）。
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdlib>
#include <fcntl.h>
#include <future>
#include <poll.h>
#include <string>
#include <thread>
#include <unistd.h>
#include <vector>

#include "hw/bus.hpp"
#include "hw/mock_master.hpp"
#include "hw/modbus_rtu.hpp"
#include "hw/rtu_master.hpp"
#include "hw/temp_controller.hpp"
#include "testing.hpp"
#include <cstdint>
#include <cstdio>
#include <memory>

using namespace reactor::hw;

namespace {

enum class Mode { Normal, Exception, BadCrc, WrongAddr, Silent };

// 假从站：在 pty 主端读请求帧，交给 MockModbusMaster 执行，再按 RTU 编回包。
class FakeSlave {
public:
    FakeSlave(int fd, MockModbusMaster& backend) : fd_(fd), backend_(backend), th_([this] { loop(); }) {}
    ~FakeSlave() {
        stop_ = true;
        th_.join();
    }
    std::atomic<Mode> mode{Mode::Normal};
    std::atomic<int> requests{0};

private:
    void loop() {
        std::vector<uint8_t> buf;
        while (!stop_) {
            pollfd p{fd_, POLLIN, 0};
            if (::poll(&p, 1, 20) <= 0) continue;
            uint8_t tmp[256];
            ssize_t n = ::read(fd_, tmp, sizeof tmp);
            if (n <= 0) continue;
            buf.insert(buf.end(), tmp, tmp + n);
            for (;;) {
                size_t len = frame_len(buf);
                if (len == 0 || buf.size() < len) break;
                std::vector<uint8_t> f(buf.begin(), buf.begin() + static_cast<long>(len));
                buf.erase(buf.begin(), buf.begin() + static_cast<long>(len));
                handle(f);
            }
        }
    }
    static size_t frame_len(const std::vector<uint8_t>& b) {
        if (b.size() < 2) return 0;
        uint8_t fc = b[1];
        if (fc >= 1 && fc <= 6) return 8;
        if (fc == 0x0F || fc == 0x10) return b.size() >= 7 ? 9u + b[6] : 0;
        return b.size();  // 不认识：整段丢掉
    }
    void handle(const std::vector<uint8_t>& f) {
        if (f.size() < 8 || !crc_valid(f)) return;
        ++requests;
        const uint8_t s = f[0], fc = f[1];
        const uint16_t addr = static_cast<uint16_t>((f[2] << 8) | f[3]);
        const uint16_t cnt = static_cast<uint16_t>((f[4] << 8) | f[5]);
        ModbusRequest q;
        switch (fc) {
            case 1: q = req_read_coils(s, addr, cnt); break;
            case 2: q = req_read_discrete_inputs(s, addr, cnt); break;
            case 3: q = req_read_holding(s, addr, cnt); break;
            case 4: q = req_read_input(s, addr, cnt); break;
            case 5: q = req_write_coil(s, addr, f[4] == 0xFF); break;
            case 6: q = req_write_register(s, addr, cnt); break;
            case 0x0F: {
                std::vector<bool> v(cnt);
                for (uint16_t i = 0; i < cnt; ++i) v[i] = (f[7 + i / 8] >> (i % 8)) & 1;
                q = req_write_coils(s, addr, v);
                break;
            }
            case 0x10: {
                std::vector<uint16_t> v(cnt);
                for (uint16_t i = 0; i < cnt; ++i) v[i] = static_cast<uint16_t>((f[7 + 2 * i] << 8) | f[8 + 2 * i]);
                q = req_write_registers(s, addr, v);
                break;
            }
            default: return;
        }
        Mode md = mode.load();
        if (md == Mode::Silent) return;
        std::vector<uint8_t> out;
        if (md == Mode::Exception) {
            out = {s, static_cast<uint8_t>(fc | 0x80), 0x02};
        } else {
            ModbusResult r = backend_.execute(q);
            out = {s, fc};
            if (fc == 1 || fc == 2) {
                std::vector<uint8_t> bytes((cnt + 7) / 8, 0);
                for (uint16_t i = 0; i < cnt; ++i)
                    if (r.bits[i]) bytes[i / 8] = static_cast<uint8_t>(bytes[i / 8] | (1u << (i % 8)));
                out.push_back(static_cast<uint8_t>(bytes.size()));
                out.insert(out.end(), bytes.begin(), bytes.end());
            } else if (fc == 3 || fc == 4) {
                out.push_back(static_cast<uint8_t>(2 * cnt));
                for (uint16_t v : r.regs) { out.push_back(static_cast<uint8_t>(v >> 8)); out.push_back(static_cast<uint8_t>(v)); }
            } else {
                out.insert(out.end(), f.begin() + 2, f.begin() + 6);  // 写：回显地址 + 值 / 个数
            }
            if (md == Mode::WrongAddr) out[0] = static_cast<uint8_t>(s + 1);
        }
        append_crc(out);
        if (md == Mode::BadCrc) out.back() ^= 0x5A;
        ssize_t w = ::write(fd_, out.data(), out.size());
        (void)w;
    }

    int fd_;
    MockModbusMaster& backend_;
    std::atomic<bool> stop_{false};
    std::thread th_;
};

struct Pty {
    int master = -1;
    std::string slave_path;
    bool open() {
        master = posix_openpt(O_RDWR | O_NOCTTY);
        if (master < 0) return false;
        if (grantpt(master) != 0 || unlockpt(master) != 0) return false;
        const char* name = ptsname(master);
        if (!name) return false;
        slave_path = name;
        return true;
    }
    ~Pty() {
        if (master >= 0) ::close(master);
    }
};

int64_t now_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

}  // namespace

TEST(hw_rtu, pty_master_transactions) {
    Pty pty;
    if (!pty.open()) {
        std::printf("  （跳过：开不了伪终端）\n");
        return;
    }
    MockModbusMaster backend;
    FakeSlave slave(pty.master, backend);
    SerialConfig sc;
    sc.path = pty.slave_path;
    sc.baud = 19200;
    RtuModbusMaster m(sc, 300);
    CHECK(!m.is_open());  // 惰性打开

    // FC06 / FC03
    auto w = m.write_register(1, 0x0002, 1500);
    CHECK(w.ok);
    if (!w.ok) std::printf("  rtu 写失败：%s\n", w.error.c_str());
    CHECK(m.is_open());
    CHECK_EQ(backend.holding(1, 0x0002), static_cast<uint16_t>(1500));
    backend.set_holding(1, 0x0600, static_cast<uint16_t>(static_cast<int16_t>(-15)));
    auto r = m.read_holding(1, 0x0600, 8);
    CHECK(r.ok && r.regs.size() == 8 && static_cast<int16_t>(r.regs[0]) == -15);
    // FC04
    backend.set_input(1, 3, 9600);
    auto ri = m.read_input(1, 0, 8);
    CHECK(ri.ok && ri.regs[3] == 9600);
    // FC0F / FC01 / FC05 / FC02
    std::vector<bool> coils(12, false);
    coils[1] = coils[11] = true;
    CHECK(m.write_coils(1, 0, coils).ok);
    CHECK(backend.coil(1, 1) && backend.coil(1, 11) && !backend.coil(1, 2));
    CHECK(m.write_coil(1, 5, true).ok);
    auto rc = m.read_coils(1, 0, 16);
    CHECK(rc.ok && rc.bits.size() == 16 && rc.bits[1] && rc.bits[5] && rc.bits[11] && !rc.bits[0]);
    backend.set_discrete(1, 9, true);
    auto rd = m.read_discrete_inputs(1, 0, 16);
    CHECK(rd.ok && rd.bits[9] && !rd.bits[8]);
    // FC10
    CHECK(m.write_registers(1, 0x6200, {2, 0, 0, 895, 1000, 1000, 0, 0x10}).ok);
    CHECK_EQ(backend.holding(1, 0x6203), static_cast<uint16_t>(895));

    // 异常码：读 2 字节头判出异常，再读 3 字节；关串口
    slave.mode = Mode::Exception;
    auto ex = m.read_holding(1, 0, 1);
    CHECK(!ex.ok && ex.exception == 0x02);
    CHECK(ex.error.find("非法数据地址") != std::string::npos);
    CHECK(!m.is_open());

    // 恢复后自动重开
    slave.mode = Mode::Normal;
    CHECK(m.read_holding(1, 0x0002, 1).ok);
    CHECK(m.is_open());

    // CRC 错
    slave.mode = Mode::BadCrc;
    auto bc = m.read_holding(1, 0, 2);
    CHECK(!bc.ok && bc.error.find("CRC") != std::string::npos);
    CHECK(!m.is_open());

    // 站号不对
    slave.mode = Mode::WrongAddr;
    auto wa = m.read_holding(1, 0, 2);
    CHECK(!wa.ok && wa.error.find("从站地址不匹配") != std::string::npos);

    // 超时：300 ms，不重试
    slave.mode = Mode::Silent;
    int before = slave.requests.load();
    int64_t t0 = now_ms();
    auto to = m.read_holding(1, 0, 2);
    int64_t dt = now_ms() - t0;
    CHECK(!to.ok && to.error.find("响应不足") != std::string::npos);
    CHECK(dt >= 280 && dt < 1500);
    CHECK_EQ(slave.requests.load() - before, 1);  // 只发了一次
    CHECK(!m.is_open());

    // 残留输入被丢弃：先往线上灌几个垃圾字节，再发事务
    slave.mode = Mode::Normal;
    CHECK(m.read_holding(1, 0, 1).ok);  // 先把串口打开
    uint8_t junk[] = {0x01, 0x03, 0x02, 0xAA};
    ssize_t jw = ::write(pty.master, junk, sizeof junk);
    (void)jw;
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    auto after = m.read_holding(1, 0x0002, 1);
    CHECK(after.ok && after.regs[0] == 1500);

    // 非法请求：不发帧
    before = slave.requests.load();
    CHECK(!m.read_holding(1, 0, 200).ok);
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    CHECK_EQ(slave.requests.load(), before);
}

TEST(hw_rtu, open_failure_and_bus_poll) {
    SerialConfig bad;
    bad.path = "/dev/does-not-exist-hw-test";
    RtuModbusMaster nope(bad);
    auto r = nope.read_holding(1, 0, 1);
    CHECK(!r.ok && r.error.find("打不开") != std::string::npos);
    SerialConfig badbaud;
    badbaud.path = "/dev/null";
    badbaud.baud = 12345;
    CHECK(!RtuModbusMaster(badbaud).read_holding(1, 0, 1).ok);

    Pty pty;
    if (!pty.open()) return;
    MockModbusMaster backend;
    FakeSlave slave(pty.master, backend);
    for (int i = 0; i < 8; ++i) backend.set_holding(1, static_cast<uint16_t>(0x0600 + i), static_cast<uint16_t>(250 + i));
    backend.set_holding(1, 0x0844, 8);
    SerialConfig sc;
    sc.path = pty.slave_path;
    sc.baud = 19200;
    Bus bus("温控", std::make_shared<RtuModbusMaster>(sc, 300));
    TempController t(1, 8, 1);
    std::promise<std::vector<ModbusResult>> p;
    bus.submit_seq(t.poll_requests(), Priority::Normal, true, [&](const std::vector<ModbusResult>& rs) { p.set_value(rs); });
    auto poll = t.decode_poll(p.get_future().get());
    CHECK(poll.has_value());
    if (poll) {
        CHECK_NEAR(poll->pv[7], 25.7, 1e-9);
        CHECK_EQ(poll->ctn, 8);
    }
    CHECK(bus.stats().online());
    CHECK_EQ(bus.stats().polls(), static_cast<int64_t>(5));
}
