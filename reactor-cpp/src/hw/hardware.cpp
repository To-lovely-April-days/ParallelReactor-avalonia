#include "hw/hardware.hpp"

#include <atomic>
#include <mutex>

#include "common/log.hpp"
#include "hw/rtu_master.hpp"
#include "hw/tracing_master.hpp"

namespace reactor::hw {

namespace {

SerialConfig serial_of(const BusConfig& b) {
    SerialConfig s;
    s.path = b.port;
    s.baud = b.baud;
    s.parity = b.parity;
    s.data_bits = b.data_bits;
    s.stop_bits = b.stop_bits;
    return s;
}

std::shared_ptr<ModbusMaster> real_or_offline(const BusConfig& b, const char* what) {
    std::shared_ptr<ModbusMaster> m;
    if (b.port.empty()) m = std::make_shared<OfflineModbusMaster>(what);
    else m = std::make_shared<RtuModbusMaster>(serial_of(b), b.timeout_ms);
    return m;
}

std::shared_ptr<ModbusMaster> maybe_trace(std::shared_ptr<ModbusMaster> m, const BusConfig& b) {
    if (b.trace.empty()) return m;
    return std::make_shared<TracingModbusMaster>(std::move(m), b.trace);
}

}  // namespace

std::unique_ptr<Hardware> make_hardware(const HardwareConfig& cfg) {
    auto hw = std::make_unique<Hardware>();
    hw->cfg = cfg;
    hw->simulated = cfg.simulated();
    hw->sim_refused = cfg.sim_refused();
    if (hw->sim_refused) LOGW("hw", "hardware.json 要求模拟但配置了串口：拒绝模拟，按真机端口运行");

    hw->temp = TempController(static_cast<uint8_t>(cfg.temp.slave), 8, cfg.temp_dpt);
    hw->press = AnalogModule(static_cast<uint8_t>(cfg.press.slave));
    hw->io = IoModule(static_cast<uint8_t>(cfg.io.slave), cfg.io_energize_opens());
    hw->stir = StepperDriver(static_cast<uint8_t>(cfg.stir.slave));

    std::shared_ptr<ModbusMaster> temp_m, stir_m, press_m, io_m;
    if (hw->simulated) {
        MockModbusMaster::Options o;
        o.temp_slaves = {static_cast<uint8_t>(cfg.temp.slave)};
        o.temp_loops = 8;  // C#: SimBus TempLoops = 8
        o.analog_slave = static_cast<uint8_t>(cfg.press.slave);
        // 气路模型：常压读 0 psi（4 mA），开阀才升压（联调 2026-11-26：原来随机游走在 8000 附近 ≈ 1.5 MPa）
        o.gas_model = true;
        o.io_slave = static_cast<uint8_t>(cfg.io.slave);
        o.io_energize_opens = cfg.io_energize_opens();
        o.fullscale_psi = cfg.press_fullscale_psi;
        o.press_4to20 = cfg.press_4to20();
        hw->sim_main = std::make_shared<MockModbusMaster>(o);
        hw->sim_stir = std::make_shared<MockModbusMaster>();
        temp_m = press_m = io_m = hw->sim_main;
        stir_m = hw->sim_stir;
        LOGW("hw", "模拟模式：没有连接任何真实设备");
    } else {
        temp_m = real_or_offline(cfg.temp, "温控器 AI-8（hardware.json temp.port）");
        stir_m = real_or_offline(cfg.stir, "搅拌驱动器（hardware.json stir.port）");
        press_m = real_or_offline(cfg.press, "压力采集 8AI（hardware.json press.port）");
        io_m = real_or_offline(cfg.io, "阀门 IO16R（hardware.json io.port）");
    }
    hw->temp_bus = std::make_unique<Bus>("温控", maybe_trace(temp_m, cfg.temp));
    hw->stir_bus = std::make_unique<Bus>("搅拌", maybe_trace(stir_m, cfg.stir));
    hw->press_bus = std::make_unique<Bus>("压力", maybe_trace(press_m, cfg.press));
    hw->io_bus = std::make_unique<Bus>("阀门", maybe_trace(io_m, cfg.io));
    return hw;
}

void Hardware::simulate_step() {
    if (sim_main) sim_main->simulate_step();
    if (sim_stir) sim_stir->simulate_step();
}

void Hardware::urgent_stop_all(bool stop_stir, std::function<void(const StopAllOutcome&)> done) {
    struct State {
        std::mutex mu;
        StopAllOutcome out;
        int pending = 0;
        std::function<void(const StopAllOutcome&)> done;
    };
    auto st = std::make_shared<State>();
    st->done = std::move(done);
    st->out.stir_requested = stop_stir;
    st->pending = stop_stir ? 3 : 2;

    auto finish = [st](auto setter) {
        std::function<void(const StopAllOutcome&)> cb;
        StopAllOutcome snapshot;
        {
            std::lock_guard<std::mutex> lk(st->mu);
            setter(st->out);
            if (--st->pending != 0) return;
            cb = st->done;
            snapshot = st->out;
        }
        if (cb) cb(snapshot);
    };

    // AI-8：逐路 At=4，失败也继续下一路（C# StopAllChannelsAsync：某路失败也继续）
    temp_bus->submit_seq(temp.stop_all_loops_requests(), Priority::Urgent, /*stop_on_fail=*/false,
                         [finish, n = static_cast<size_t>(temp.loops())](const std::vector<ModbusResult>& rs) {
                             bool ok = rs.size() == n;
                             for (const auto& r : rs) ok = ok && r.ok;
                             finish([ok](StopAllOutcome& o) { o.heat_ok = ok; });
                         });
    io_bus->submit(io.req_close_all_valves(), Priority::Urgent, [finish](const ModbusResult& r) {
        bool ok = r.ok;
        finish([ok](StopAllOutcome& o) { o.valves_ok = ok; });
    });
    if (stop_stir) {
        stir_bus->submit(stir.req_stop(), Priority::Urgent, [finish](const ModbusResult& r) {
            bool ok = r.ok;
            finish([ok](StopAllOutcome& o) { o.stir_ok = ok; });
        });
    }
}

void Hardware::stop() {
    for (Bus* b : {temp_bus.get(), stir_bus.get(), press_bus.get(), io_bus.get()})
        if (b) b->stop(false);
}

Hardware::~Hardware() { stop(); }

}  // namespace reactor::hw
