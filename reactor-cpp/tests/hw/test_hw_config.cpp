// hardware.json：默认值（照 run.sh + 代码默认波特率）、读写往返、部分字段、非法值、模拟拒绝
#include <cstdio>
#include <cstdlib>
#include <string>
#include <unistd.h>

#include "hw/hardware_config.hpp"
#include "testing.hpp"

using namespace reactor::hw;
using nlohmann::json;

namespace {
std::string temp_dir() {
    const char* base = std::getenv("TMPDIR");
    std::string tmpl = std::string(base && *base ? base : "/tmp") + "/hwcfgXXXXXX";
    std::vector<char> buf(tmpl.begin(), tmpl.end());
    buf.push_back('\0');
    char* d = mkdtemp(buf.data());
    return d ? std::string(d) : std::string("/tmp");
}
void write_file(const std::string& p, const std::string& s) {
    FILE* f = std::fopen(p.c_str(), "wb");
    if (!f) return;
    std::fwrite(s.data(), 1, s.size(), f);
    std::fclose(f);
}
}  // namespace

TEST(hw_config, defaults_match_run_sh) {
    HardwareConfig c;
    CHECK(!c.sim);
    CHECK_EQ(c.temp.port, std::string("/dev/ttyS5"));
    CHECK_EQ(c.stir.port, std::string("/dev/ttyS4"));
    CHECK_EQ(c.press.port, std::string("/dev/ttyS6"));
    CHECK_EQ(c.io.port, std::string("/dev/ttyS8"));
    CHECK_EQ(c.temp.baud, 19200);
    CHECK_EQ(c.stir.baud, 38400);
    CHECK_EQ(c.press.baud, 9600);
    CHECK_EQ(c.io.baud, 9600);
    for (const BusConfig* b : {&c.temp, &c.stir, &c.press, &c.io}) {
        CHECK_EQ(b->slave, 1);
        CHECK_EQ(b->timeout_ms, 300);
        CHECK(b->parity == 'N' && b->data_bits == 8 && b->stop_bits == 1);
        CHECK(b->trace.empty());
    }
    CHECK_EQ(c.temp_dpt, 1);
    CHECK(c.press_4to20());
    CHECK_NEAR(c.press_fullscale_psi, 600.0, 1e-9);
    CHECK(c.io_energize_opens());
    std::string err;
    CHECK(validate_hardware_config(c, &err));

    // 模拟：配了端口就拒绝
    c.sim = true;
    CHECK(c.sim_refused() && !c.simulated());
    auto s = HardwareConfig::for_sim();
    CHECK(s.simulated() && !s.sim_refused());
}

TEST(hw_config, roundtrip_and_partial) {
    std::string dir = temp_dir();
    std::string path = dir + "/hardware.json";
    HardwareConfig c;
    c.temp.baud = 9600;
    c.stir.trace = "stir-trace.log";
    c.press_signal = "0-10";
    c.press_fullscale_psi = 1000;
    c.io_energize = "close";
    c.io.parity = 'E';
    c.temp_dpt = 2;
    std::string err;
    CHECK(save_hardware_config(path, c, &err));
    HardwareConfig back;
    CHECK(load_hardware_config(path, back, &err));
    CHECK_EQ(back.temp.baud, 9600);
    CHECK_EQ(back.stir.trace, std::string("stir-trace.log"));
    CHECK(!back.press_4to20());
    CHECK_NEAR(back.press_fullscale_psi, 1000.0, 1e-9);
    CHECK(!back.io_energize_opens());
    CHECK(back.io.parity == 'E');
    CHECK_EQ(back.temp_dpt, 2);

    // 只写部分字段：其余取默认
    json j = json::parse(R"({"sim": true, "temp": {"port": "", "baud": 9600}, "stir": {"port": ""},
                             "press": {"port": ""}, "io": {"port": "", "energize": "close"}})");
    HardwareConfig p;
    CHECK(hardware_config_from_json(j, p, &err));
    CHECK(p.simulated());
    CHECK_EQ(p.temp.baud, 9600);
    CHECK_EQ(p.stir.baud, 38400);
    CHECK_EQ(p.temp.timeout_ms, 300);
    CHECK(!p.io_energize_opens());

    // 空对象 = 全默认
    HardwareConfig e;
    CHECK(hardware_config_from_json(json::object(), e, &err));
    CHECK_EQ(e.io.port, std::string("/dev/ttyS8"));

    // JSON 里的 key 名
    json out = c;
    CHECK(out["temp"].contains("timeoutMs") && out["press"].contains("fullScalePsi") && out["io"].contains("energize"));
    CHECK_EQ(out["version"].get<int>(), 1);

    std::remove(path.c_str());
    rmdir(dir.c_str());
}

TEST(hw_config, invalid_values_rejected) {
    std::string err;
    HardwareConfig out;
    out.temp.baud = 4800;  // 用来确认失败时 out 不被改
    CHECK(!hardware_config_from_json(json::parse(R"({"temp":{"baud":12345}})"), out, &err));
    CHECK(err.find("baud") != std::string::npos);
    CHECK_EQ(out.temp.baud, 4800);
    CHECK(!hardware_config_from_json(json::parse(R"({"temp":{"baud":"fast"}})"), out, &err));
    CHECK(err.find("类型") != std::string::npos);
    CHECK(!hardware_config_from_json(json::parse(R"({"io":{"parity":"X"}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"io":{"slave":0}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"press":{"signal":"5-10"}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"press":{"fullScalePsi":0}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"io":{"energize":"maybe"}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"stir":{"timeoutMs":10}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"temp":{"dpt":5}})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"({"temp":5})"), out, &err));
    CHECK(!hardware_config_from_json(json::parse(R"([1,2])"), out, &err));

    std::string dir = temp_dir();
    HardwareConfig c;
    CHECK(!load_hardware_config(dir + "/missing.json", c, &err));
    write_file(dir + "/bad.json", "{ not json");
    CHECK(!load_hardware_config(dir + "/bad.json", c, &err));
    CHECK(err.find("JSON") != std::string::npos);
    std::remove((dir + "/bad.json").c_str());
    rmdir(dir.c_str());
}
