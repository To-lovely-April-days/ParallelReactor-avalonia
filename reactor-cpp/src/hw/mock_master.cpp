#include "hw/mock_master.hpp"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>

namespace reactor::hw {

namespace {
template <class M, class V>
V get_or(const M& m, uint32_t k, V def) {
    auto it = m.find(k);
    return it == m.end() ? def : it->second;
}
}  // namespace

MockModbusMaster::MockModbusMaster() : MockModbusMaster(Options{}) {}

MockModbusMaster::MockModbusMaster(Options opt) : opt_(std::move(opt)) {
    rng_.seed(opt_.seed != 0 ? opt_.seed : std::random_device{}());
}

int MockModbusMaster::rnd(int lo, int hi_exclusive) {
    std::uniform_int_distribution<int> d(lo, hi_exclusive - 1);
    return d(rng_);
}

ModbusResult MockModbusMaster::execute(const ModbusRequest& req) {
    std::lock_guard<std::mutex> lk(mu_);
    ++txns_;
    last_ = req;
    if (req.is_write()) writes_.push_back(req);
    std::string bad = validate_request(req);
    if (!bad.empty()) return fail_result("请求无效：" + bad);
    if (fail_all_) return fail_result("模拟：通讯失败");
    if (fail_next_ > 0) { --fail_next_; return fail_result("模拟：通讯失败"); }

    const uint8_t s = req.slave;
    // 气路模型：读压力前推进到现在；写阀门前先按旧阀态积分到现在
    if (opt_.gas_model) {
        if ((req.fc == kFcReadInput && s == opt_.analog_slave) ||
            ((req.fc == kFcWriteCoil || req.fc == kFcWriteCoils) && s == opt_.io_slave)) {
            advance_gas_locked();
            publish_pressure_locked();
        }
    }
    ModbusResult r;
    r.ok = true;
    switch (req.fc) {
        case kFcReadCoils:
        case kFcReadDiscreteInputs: {
            const auto& m = req.fc == kFcReadCoils ? coil_ : di_;
            r.bits.resize(req.count);
            for (uint16_t i = 0; i < req.count; ++i)
                r.bits[i] = get_or(m, key(s, static_cast<uint16_t>(req.addr + i)), false);
            break;
        }
        case kFcReadHolding:
        case kFcReadInput: {
            const auto& m = req.fc == kFcReadHolding ? hold_ : input_;
            r.regs.resize(req.count);
            for (uint16_t i = 0; i < req.count; ++i)
                r.regs[i] = get_or(m, key(s, static_cast<uint16_t>(req.addr + i)), static_cast<uint16_t>(0));
            break;
        }
        case kFcWriteCoil: coil_[key(s, req.addr)] = req.bits[0]; break;
        case kFcWriteRegister: hold_[key(s, req.addr)] = req.regs[0]; break;
        case kFcWriteCoils:
            for (size_t i = 0; i < req.bits.size(); ++i) coil_[key(s, static_cast<uint16_t>(req.addr + i))] = req.bits[i];
            break;
        case kFcWriteRegisters:
            for (size_t i = 0; i < req.regs.size(); ++i) hold_[key(s, static_cast<uint16_t>(req.addr + i))] = req.regs[i];
            break;
        default: break;
    }
    return r;
}

void MockModbusMaster::seed_temp() {
    // 给温控站预置一组默认 PID，使界面初始就有合理数值（C#: SeedTemp，TryAdd = 已有值不覆盖）
    for (uint8_t s : opt_.temp_slaves) {
        for (int i = 0; i < opt_.temp_loops; ++i) {
            hold_.emplace(key(s, static_cast<uint16_t>(0x0060 + i)), 600);   // P = 60.0
            hold_.emplace(key(s, static_cast<uint16_t>(0x00C0 + i)), 1200);  // I = 120.0 s
            hold_.emplace(key(s, static_cast<uint16_t>(0x0120 + i)), 2000);  // D = 20.0 s
        }
        hold_.emplace(key(s, 0x0844), static_cast<uint16_t>(opt_.temp_loops));  // Ctn 启用回路数
        hold_.emplace(key(s, 0x0845), 0);                                        // Srun 运行
    }
}

void MockModbusMaster::simulate_step() {
    std::lock_guard<std::mutex> lk(mu_);
    if (!seeded_) { seed_temp(); seeded_ = true; }

    for (uint8_t s : opt_.temp_slaves) {
        for (int i = 0; i < opt_.temp_loops; ++i) {
            // PV 向 SP 漂移；通道停止（At=4）或全局停止（Srun=9655）时输出关闭，PV 自然冷却回室温 25.0℃
            int sp = static_cast<int16_t>(get_or(hold_, key(s, static_cast<uint16_t>(0x0000 + i)), static_cast<uint16_t>(250)));
            const bool stopped = get_or(hold_, key(s, static_cast<uint16_t>(0x0300 + i)), static_cast<uint16_t>(0)) == 4 ||
                                 get_or(hold_, key(s, 0x0845), static_cast<uint16_t>(0)) == 9655;
            if (stopped) sp = 250;
            const uint32_t pv_key = key(s, static_cast<uint16_t>(0x0600 + i));
            int pv = static_cast<int16_t>(get_or(hold_, pv_key, static_cast<uint16_t>(static_cast<int16_t>(sp))));
            const int diff = sp - pv;
            const int step = (diff > 0 ? 1 : diff < 0 ? -1 : 0) * std::min(std::abs(diff), 5);
            pv = static_cast<int16_t>(pv + step + rnd(-1, 2));
            hold_[pv_key] = static_cast<uint16_t>(static_cast<int16_t>(pv));
            // 输出 OP（25600 = 100%）：只在 reactord --sim 的模型里给。没有它防超调逼近会把「输出 ≈ 0 仍在升温」当成放热
            if (opt_.gas_model) {
                // 比例项 8 %/℃ + 维持项 0.6 %/℃（高于室温 25 ℃ 的部分）：恒温在 30 ℃ 以上时输出就明显大于「≈0」判据 2 %
                double pct = stopped ? 0.0 : std::clamp(8.0 * (sp - pv) / 10.0 + 0.6 * (sp - 250) / 10.0, 0.0, 100.0);
                hold_[key(s, static_cast<uint16_t>(0x0360 + i))] = static_cast<uint16_t>(std::lround(pct * 256.0));
            }

            // 自整定模拟：At=1 持续若干周期后回 0，并写入一组 PID 结果
            const uint32_t at_key = key(s, static_cast<uint16_t>(0x0300 + i));
            if (get_or(hold_, at_key, static_cast<uint16_t>(0)) == 1) {
                int n = get_or(tune_count_, at_key, 0) + 1;
                tune_count_[at_key] = n;
                if (n >= 6) {  // ≈ 7 秒（按 1.2 s tick）
                    tune_count_[at_key] = 0;
                    hold_[at_key] = 0;  // 整定结束，回 APID
                    hold_[key(s, static_cast<uint16_t>(0x0060 + i))] = static_cast<uint16_t>(700 + rnd(0, 200));   // P
                    hold_[key(s, static_cast<uint16_t>(0x00C0 + i))] = static_cast<uint16_t>(1000 + rnd(0, 400));  // I（0.1s）
                    hold_[key(s, static_cast<uint16_t>(0x0120 + i))] = static_cast<uint16_t>(1500 + rnd(0, 800));  // D（0.01s）
                }
            }
        }
    }

    if (opt_.gas_model && opt_.analog_slave != 0) {
        advance_gas_locked();
        publish_pressure_locked();
    } else if (opt_.analog_slave != 0) {
        for (int i = 0; i < 8; ++i) {
            const uint32_t k = key(opt_.analog_slave, static_cast<uint16_t>(i));
            int v = get_or(input_, k, static_cast<uint16_t>(8000)) + rnd(-30, 31);
            input_[k] = static_cast<uint16_t>(std::clamp(v, 3200, 16000));
        }
    }
}

void MockModbusMaster::advance_gas_locked() {
    using namespace std::chrono;
    const int64_t now = duration_cast<microseconds>(steady_clock::now().time_since_epoch()).count();
    if (gas_t_us_ < 0) {
        gas_t_us_ = now;
        for (int i = 0; i < 8; ++i) uptake_left_[i] = 40 + 4 * i;  // 每釜吸气量不同，曲线好区分
        return;
    }
    double dt_total = std::min(10.0, static_cast<double>(now - gas_t_us_) / 1e6);
    gas_t_us_ = now;
    integrate_gas_locked(dt_total);
}

void MockModbusMaster::simulate_gas(double sec) {
    std::lock_guard<std::mutex> lk(mu_);
    if (gas_t_us_ < 0) advance_gas_locked();
    integrate_gas_locked(sec);
    publish_pressure_locked();
}

void MockModbusMaster::integrate_gas_locked(double dt_total) {
    if (dt_total <= 0) return;
    bool v[12];
    for (int i = 0; i < 12; ++i) {
        bool coil = get_or(coil_, key(opt_.io_slave, static_cast<uint16_t>(i)), false);
        v[i] = opt_.io_energize_opens ? coil : !coil;
    }
    int gas = -1;
    for (int g = 0; g < 3; ++g)
        if (v[8 + g]) gas = g;
    const bool vent = v[11];
    constexpr double kVm = 2.0, kVg = 8.0;  // 总管 / 釜气相体积 mL
    constexpr double kK = 0.08;             // 釜与总管的交换速率 1/s（针阀限流：480 psi 气源开始约 38 psi/s，脉冲充压不冲过头）
    constexpr double kKOut = 0.5;           // 釜 → 总管（放空）1/s
    constexpr double kLeak = 0.4;           // 每釜自然泄漏 psi/h（远低于泄漏测试判据）
    constexpr double kReact = 1.0 / 600;    // 吸气一级反应速率 1/s（时间常数 10 min）
    for (double done = 0; done < dt_total;) {
        const double dt = std::min(0.01, dt_total - done);
        done += dt;
        const double a = std::min(1.0, dt / 0.05);
        if (gas >= 0) pm_ += (opt_.supply_psi[gas] - pm_) * a;
        else if (vent) pm_ += (0 - pm_) * a;
        for (int i = 0; i < 8; ++i) {
            if (v[i]) {
                // 进釜经减压阀 + 针阀限流（慢）；出釜经排空直通（快，6 s 就能泄掉大半——泄漏测试结束时按这个核实泄压）
                const double d = (pm_ > p_[i] ? kK : kKOut) * dt * (pm_ - p_[i]);
                p_[i] += d;
                if (gas < 0 && !vent) pm_ -= d * kVg / kVm;
                if (gas >= 1 && d > 0) reactive_[i] = true;
            }
            p_[i] = std::max(0.0, p_[i] - kLeak / 3600.0 * dt);
        }
    }
    // 吸气（按整段 dt 算一次就够）：釜热且充过反应气体时压力按一级反应下降；放空到常压后重置
    for (int i = 0; i < 8; ++i) {
        const double pv = static_cast<int16_t>(get_or(hold_, key(opt_.temp_slaves.empty() ? 1 : opt_.temp_slaves[0],
                                                                 static_cast<uint16_t>(0x0600 + i)), static_cast<uint16_t>(250))) / 10.0;
        if (p_[i] < 3) {
            uptake_left_[i] = 40 + 4 * i;
            reactive_[i] = false;
            continue;
        }
        if (!reactive_[i] || pv < 60 || v[i]) continue;
        const double d = std::min({uptake_left_[i] * (1 - std::exp(-kReact * dt_total)), uptake_left_[i], p_[i] - 1});
        if (d > 0) { p_[i] -= d; uptake_left_[i] -= d; }
    }
}

void MockModbusMaster::publish_pressure_locked() {
    const double zero = opt_.press_4to20 ? 3200.0 : 0.0;
    const double per_psi = (16000.0 - zero) / opt_.fullscale_psi;
    for (int i = 0; i < 8; ++i) {
        int raw = static_cast<int>(std::lround(zero + p_[i] * per_psi)) + rnd(-1, 2);  // ±1 原码 ≈ ±0.05 psi（变送器 + 模块滤波后的量级）
        input_[key(opt_.analog_slave, static_cast<uint16_t>(i))] = static_cast<uint16_t>(std::clamp(raw, 0, 65535));
    }
}

uint16_t MockModbusMaster::holding(uint8_t s, uint16_t a) const {
    std::lock_guard<std::mutex> lk(mu_);
    return get_or(hold_, key(s, a), static_cast<uint16_t>(0));
}
void MockModbusMaster::set_holding(uint8_t s, uint16_t a, uint16_t v) {
    std::lock_guard<std::mutex> lk(mu_);
    hold_[key(s, a)] = v;
}
uint16_t MockModbusMaster::input(uint8_t s, uint16_t a) const {
    std::lock_guard<std::mutex> lk(mu_);
    return get_or(input_, key(s, a), static_cast<uint16_t>(0));
}
void MockModbusMaster::set_input(uint8_t s, uint16_t a, uint16_t v) {
    std::lock_guard<std::mutex> lk(mu_);
    input_[key(s, a)] = v;
    // 气路模型：后门写压力原码 = 直接设这一釜的压力（之后按阀态继续演化）
    if (opt_.gas_model && s == opt_.analog_slave && a < 8) {
        if (gas_t_us_ < 0) advance_gas_locked();
        const double zero = opt_.press_4to20 ? 3200.0 : 0.0;
        p_[a] = std::max(0.0, (static_cast<double>(v) - zero) * opt_.fullscale_psi / (16000.0 - zero));
    }
}
bool MockModbusMaster::coil(uint8_t s, uint16_t a) const {
    std::lock_guard<std::mutex> lk(mu_);
    return get_or(coil_, key(s, a), false);
}
void MockModbusMaster::set_coil(uint8_t s, uint16_t a, bool v) {
    std::lock_guard<std::mutex> lk(mu_);
    coil_[key(s, a)] = v;
}
void MockModbusMaster::set_discrete(uint8_t s, uint16_t a, bool v) {
    std::lock_guard<std::mutex> lk(mu_);
    di_[key(s, a)] = v;
}
void MockModbusMaster::set_fail_all(bool fail) {
    std::lock_guard<std::mutex> lk(mu_);
    fail_all_ = fail;
}
void MockModbusMaster::fail_next(int n) {
    std::lock_guard<std::mutex> lk(mu_);
    fail_next_ = n;
}
int64_t MockModbusMaster::txn_count() const {
    std::lock_guard<std::mutex> lk(mu_);
    return txns_;
}
ModbusRequest MockModbusMaster::last_request() const {
    std::lock_guard<std::mutex> lk(mu_);
    return last_;
}
std::vector<ModbusRequest> MockModbusMaster::writes() const {
    std::lock_guard<std::mutex> lk(mu_);
    return writes_;
}
void MockModbusMaster::clear_writes() {
    std::lock_guard<std::mutex> lk(mu_);
    writes_.clear();
}

}  // namespace reactor::hw
