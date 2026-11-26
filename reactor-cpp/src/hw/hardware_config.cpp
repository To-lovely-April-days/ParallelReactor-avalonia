#include "hw/hardware_config.hpp"

#include <cerrno>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <sstream>
#include <algorithm>
#include <string>

namespace reactor::hw {

using nlohmann::json;

HardwareConfig HardwareConfig::for_sim() {
    HardwareConfig c;
    c.sim = true;
    c.temp.port.clear();
    c.stir.port.clear();
    c.press.port.clear();
    c.io.port.clear();
    return c;
}

static json bus_to_json(const BusConfig& b) {
    return json{{"port", b.port},           {"baud", b.baud},         {"slave", b.slave},
                {"parity", std::string(1, b.parity)}, {"dataBits", b.data_bits}, {"stopBits", b.stop_bits},
                {"timeoutMs", b.timeout_ms}, {"trace", b.trace}};
}

void to_json(json& j, const HardwareConfig& c) {
    j = json::object();
    j["version"] = 1;
    j["sim"] = c.sim;
    j["temp"] = bus_to_json(c.temp);
    j["temp"]["dpt"] = c.temp_dpt;
    j["stir"] = bus_to_json(c.stir);
    j["press"] = bus_to_json(c.press);
    j["press"]["signal"] = c.press_signal;
    j["press"]["fullScalePsi"] = c.press_fullscale_psi;
    j["io"] = bus_to_json(c.io);
    j["io"]["energize"] = c.io_energize;
}

namespace {

// 取可选字段：缺省不动，类型不对报错。
template <class T>
bool opt_field(const json& o, const char* key, T& dst, const std::string& where, std::string* err) {
    auto it = o.find(key);
    if (it == o.end() || it->is_null()) return true;
    try {
        dst = it->get<T>();
        return true;
    } catch (const std::exception&) {
        if (err) *err = "hardware.json 字段 " + where + "." + key + " 类型不对";
        return false;
    }
}

bool bus_from_json(const json& j, const char* name, BusConfig& b, std::string* err) {
    auto it = j.find(name);
    if (it == j.end() || it->is_null()) return true;
    if (!it->is_object()) {
        if (err) *err = std::string("hardware.json 字段 ") + name + " 应为对象";
        return false;
    }
    const json& o = *it;
    std::string parity(1, b.parity);
    if (!opt_field(o, "port", b.port, name, err) || !opt_field(o, "baud", b.baud, name, err) ||
        !opt_field(o, "slave", b.slave, name, err) || !opt_field(o, "parity", parity, name, err) ||
        !opt_field(o, "dataBits", b.data_bits, name, err) || !opt_field(o, "stopBits", b.stop_bits, name, err) ||
        !opt_field(o, "timeoutMs", b.timeout_ms, name, err) || !opt_field(o, "trace", b.trace, name, err))
        return false;
    if (parity.size() != 1) {
        if (err) *err = std::string("hardware.json 字段 ") + name + ".parity 应为 N / E / O";
        return false;
    }
    b.parity = parity[0];
    return true;
}

bool baud_supported(int b) {
    switch (b) {
        case 1200: case 2400: case 4800: case 9600: case 19200: case 38400: case 57600: case 115200: return true;
        default: return false;
    }
}

bool validate_bus(const BusConfig& b, const char* name, std::string* err) {
    auto bad = [&](const std::string& what) {
        if (err) *err = std::string("hardware.json ") + name + "." + what;
        return false;
    };
    if (!baud_supported(b.baud)) return bad("baud 不支持：" + std::to_string(b.baud));
    if (b.slave < 1 || b.slave > 247) return bad("slave 应为 1–247");
    if (b.parity != 'N' && b.parity != 'E' && b.parity != 'O') return bad("parity 应为 N / E / O");
    if (b.data_bits != 7 && b.data_bits != 8) return bad("dataBits 应为 7 或 8");
    if (b.stop_bits != 1 && b.stop_bits != 2) return bad("stopBits 应为 1 或 2");
    if (b.timeout_ms < 50 || b.timeout_ms > 5000) return bad("timeoutMs 应为 50–5000");
    return true;
}

}  // namespace

bool validate_hardware_config(const HardwareConfig& c, std::string* err) {
    if (!validate_bus(c.temp, "temp", err) || !validate_bus(c.stir, "stir", err) || !validate_bus(c.press, "press", err) ||
        !validate_bus(c.io, "io", err))
        return false;
    if (c.temp_dpt < 0 || c.temp_dpt > 3) { if (err) *err = "hardware.json temp.dpt 应为 0–3"; return false; }
    if (c.press_signal != "4-20" && c.press_signal != "0-10") {
        if (err) *err = "hardware.json press.signal 应为 \"4-20\" 或 \"0-10\"";
        return false;
    }
    if (!std::isfinite(c.press_fullscale_psi) || c.press_fullscale_psi <= 0) {
        if (err) *err = "hardware.json press.fullScalePsi 应大于 0";
        return false;
    }
    if (c.io_energize != "open" && c.io_energize != "close") {
        if (err) *err = "hardware.json io.energize 应为 \"open\" 或 \"close\"";
        return false;
    }
    return true;
}

bool hardware_config_from_json(const json& j, HardwareConfig& out, std::string* err) {
    if (!j.is_object()) { if (err) *err = "hardware.json 顶层应为对象"; return false; }
    HardwareConfig c;  // 从默认值开始
    if (!opt_field(j, "sim", c.sim, "", err)) return false;
    if (!bus_from_json(j, "temp", c.temp, err) || !bus_from_json(j, "stir", c.stir, err) ||
        !bus_from_json(j, "press", c.press, err) || !bus_from_json(j, "io", c.io, err))
        return false;
    if (j.contains("temp") && j["temp"].is_object() && !opt_field(j["temp"], "dpt", c.temp_dpt, "temp", err)) return false;
    if (j.contains("press") && j["press"].is_object()) {
        if (!opt_field(j["press"], "signal", c.press_signal, "press", err)) return false;
        if (!opt_field(j["press"], "fullScalePsi", c.press_fullscale_psi, "press", err)) return false;
    }
    if (j.contains("io") && j["io"].is_object() && !opt_field(j["io"], "energize", c.io_energize, "io", err)) return false;
    if (!validate_hardware_config(c, err)) return false;
    out = c;
    return true;
}

bool load_hardware_config(const std::string& path, HardwareConfig& out, std::string* err) {
    std::ifstream f(path, std::ios::binary);
    if (!f) { if (err) *err = "读不出 " + path + "：" + std::strerror(errno); return false; }
    std::stringstream ss;
    ss << f.rdbuf();
    json j = json::parse(ss.str(), nullptr, /*allow_exceptions=*/false, /*ignore_comments=*/true);
    if (j.is_discarded()) { if (err) *err = path + " 不是合法的 JSON"; return false; }
    return hardware_config_from_json(j, out, err);
}

bool save_hardware_config(const std::string& path, const HardwareConfig& c, std::string* err) {
    json j = c;
    const std::string text = j.dump(2) + "\n";
    const std::string tmp = path + ".tmp";
    FILE* f = std::fopen(tmp.c_str(), "wb");
    if (!f) { if (err) *err = "写不了 " + tmp + "：" + std::strerror(errno); return false; }
    bool ok = std::fwrite(text.data(), 1, text.size(), f) == text.size();
    ok = (std::fflush(f) == 0) && ok;
    ok = (std::fclose(f) == 0) && ok;
    if (!ok) { if (err) *err = "写 " + tmp + " 失败"; std::remove(tmp.c_str()); return false; }
    if (std::rename(tmp.c_str(), path.c_str()) != 0) {
        if (err) *err = "替换 " + path + " 失败：" + std::strerror(errno);
        std::remove(tmp.c_str());
        return false;
    }
    return true;
}

}  // namespace reactor::hw
