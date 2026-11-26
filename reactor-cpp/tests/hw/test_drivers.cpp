// 4 个设备驱动的寄存器编码 / 解码（对着模拟主站）+ 模拟主站本身 + 搅拌传动换算（C# StirGearTests 翻译）
#include <cstdlib>

#include "hw/analog_module.hpp"
#include "hw/io_module.hpp"
#include "hw/mock_master.hpp"
#include "hw/stepper_driver.hpp"
#include "hw/temp_controller.hpp"
#include "testing.hpp"
#include <cstdint>
#include <vector>

using namespace reactor::hw;

// ===================== AI-8 =====================

TEST(hw_temp, setpoint_and_scaling) {
    MockModbusMaster m;
    TempController t(1, 8, 1);
    CHECK(t.set_setpoint(m, 3, 150.0));
    CHECK_EQ(m.holding(1, 0x0002), static_cast<uint16_t>(1500));
    CHECK(t.set_setpoint(m, 1, -10.5));
    CHECK_EQ(m.holding(1, 0x0000), static_cast<uint16_t>(0xFF97));  // -105 有符号
    CHECK_NEAR(*t.read_setpoint(m, 1), -10.5, 1e-9);
    CHECK(t.set_setpoint(m, 8, 12.25));                            // 122.5 → 五成双 → 122（C# Math.Round）
    CHECK_EQ(m.holding(1, 0x0007), static_cast<uint16_t>(122));
    auto q = t.req_write_sp(2, 80.0);
    CHECK(q.fc == kFcWriteRegister && q.addr == 0x0001 && q.regs[0] == 800);

    TempController t0(1, 8, 0), t2(1, 8, 2);
    CHECK_EQ(t0.req_write_sp(1, 150.4).regs[0], static_cast<uint16_t>(150));
    CHECK_EQ(t2.req_write_sp(1, 150.0).regs[0], static_cast<uint16_t>(15000));
    CHECK_NEAR(t2.from_raw(-1234), -12.34, 1e-9);
}

TEST(hw_temp, block_reads_and_poll) {
    MockModbusMaster m;
    TempController t(1, 8, 1);
    for (int i = 0; i < 8; ++i) {
        m.set_holding(1, static_cast<uint16_t>(0x0600 + i), static_cast<uint16_t>(static_cast<int16_t>(250 + i * 100 - (i == 7 ? 1500 : 0))));
        m.set_holding(1, static_cast<uint16_t>(0x0360 + i), static_cast<uint16_t>(i * 3200));
        m.set_holding(1, static_cast<uint16_t>(0x0300 + i), static_cast<uint16_t>(i % 5));
        m.set_holding(1, static_cast<uint16_t>(0x0480 + i), static_cast<uint16_t>(800 + i));
    }
    m.set_holding(1, 0x0845, 15);
    m.set_holding(1, 0x0844, 8);

    auto pv = t.read_all_pv(m);
    CHECK(pv && pv->size() == 8);
    CHECK_NEAR((*pv)[0], 25.0, 1e-9);
    CHECK_NEAR((*pv)[7], -55.0, 1e-9);  // 250+700-1500 = -550（有符号）
    auto out = t.read_all_outputs(m);
    CHECK_NEAR((*out)[1], 12.5, 1e-9);   // 3200/256
    CHECK_NEAR((*out)[0], 0.0, 1e-9);
    auto sv = t.read_all_sv(m);
    CHECK_NEAR((*sv)[3], 80.3, 1e-9);

    auto p = t.poll(m);
    CHECK(p.has_value());
    if (p) {
        CHECK_EQ(p->srun, 15);
        CHECK_EQ(p->ctn, 8);
        CHECK_EQ(p->at[4], 4);
        CHECK_EQ(p->pv_raw[1], static_cast<int16_t>(350));
        CHECK_NEAR(p->pv[1], 35.0, 1e-9);
        CHECK_NEAR(p->op_pct[7], 87.5, 1e-9);
    }
    // 5 笔块读的顺序和地址（C# TempService.PollAsync）
    auto rq = t.poll_requests();
    CHECK(rq.size() == 5);
    CHECK(rq[0].addr == 0x0600 && rq[0].count == 8 && rq[0].fc == kFcReadHolding);
    CHECK(rq[1].addr == 0x0360 && rq[2].addr == 0x0300 && rq[3].addr == 0x0845 && rq[4].addr == 0x0844);
    CHECK(rq[3].count == 1 && rq[4].count == 1);

    // 一轮里任一笔失败 → 整轮失败，且停在失败那笔
    int64_t before = m.txn_count();
    m.fail_next(1);
    CHECK(!t.poll(m).has_value());
    CHECK_EQ(m.txn_count() - before, static_cast<int64_t>(1));
    // 个数不对也不解码
    ModbusResult r5;
    r5.ok = true;
    r5.regs = {1, 2, 3};
    CHECK(!t.decode_raw_block(r5).has_value());
}

TEST(hw_temp, modes_srun_ctn) {
    MockModbusMaster m;
    TempController t(1, 8, 1);
    CHECK(t.start(m, 2) && m.holding(1, 0x0301) == 0);
    CHECK(t.stop(m, 2) && m.holding(1, 0x0301) == 4);
    CHECK(t.autotune(m, 5) && m.holding(1, 0x0304) == 1);
    CHECK_EQ(*t.read_mode(m, 5), 1);
    CHECK(t.stop_all(m) && m.holding(1, 0x0845) == 9655);
    CHECK_EQ(*t.read_srun(m), 9655);
    CHECK(t.run_all(m) && m.holding(1, 0x0845) == 0);
    CHECK(t.set_ctn(m, 8) && *t.read_ctn(m) == 8);
    auto stops = t.stop_all_loops_requests();
    CHECK(stops.size() == 8);
    for (int i = 0; i < 8; ++i) CHECK(stops[i].addr == 0x0300 + i && stops[i].regs[0] == 4 && stops[i].fc == kFcWriteRegister);
    m.set_holding(1, 0x0360, 25600);
    CHECK_NEAR(*t.read_output(m, 1), 100.0, 1e-9);
    m.set_holding(1, 0x0603, static_cast<uint16_t>(static_cast<int16_t>(-15)));
    CHECK_NEAR(*t.read_pv(m, 4), -1.5, 1e-9);
}

TEST(hw_temp, pid_encode_decode) {
    MockModbusMaster m;
    TempController t(1, 8, 1);
    CHECK(t.set_pid(m, 2, 60.5, 120.3, -1.5));
    CHECK_EQ(m.holding(1, 0x0061), static_cast<uint16_t>(605));   // P ×10^dPt
    CHECK_EQ(m.holding(1, 0x00C1), static_cast<uint16_t>(1203));  // I ×10
    CHECK_EQ(m.holding(1, 0x0121), static_cast<uint16_t>(0xFF6A)); // D ×100 有符号（-150）
    auto pid = t.read_pid(m, 2);
    CHECK(pid.has_value());
    CHECK_NEAR(pid->p, 60.5, 1e-9);
    CHECK_NEAR(pid->i, 120.3, 1e-9);
    CHECK_NEAR(pid->d, -1.5, 1e-9);
    // 写的顺序 P → I → D
    auto w = t.req_write_pid(1, 1, 2, 3);
    CHECK(w[0].addr == 0x0060 && w[1].addr == 0x00C0 && w[2].addr == 0x0120);
    // 写一半失败：set_pid 返回 false，只发了 1 笔
    m.clear_writes();
    m.fail_next(1);
    CHECK(!t.set_pid(m, 3, 1, 2, 3));
    CHECK_EQ(m.writes().size(), static_cast<size_t>(1));
}

// ===================== 模拟主站 =====================

TEST(hw_mock, simulate_pv_drift_and_seed) {
    MockModbusMaster::Options o;
    o.temp_slaves = {1};
    o.temp_loops = 8;
    o.analog_slave = 1;
    o.seed = 12345;
    MockModbusMaster m(o);
    TempController t(1, 8, 1);
    // 首步预置：PID / Ctn / Srun（C# SeedTemp）
    m.set_holding(1, 0x0061, 777);  // 已有值不覆盖
    m.simulate_step();
    CHECK_EQ(m.holding(1, 0x0060), static_cast<uint16_t>(600));
    CHECK_EQ(m.holding(1, 0x0061), static_cast<uint16_t>(777));
    CHECK_EQ(m.holding(1, 0x00C0), static_cast<uint16_t>(1200));
    CHECK_EQ(m.holding(1, 0x0120), static_cast<uint16_t>(2000));
    CHECK_EQ(m.holding(1, 0x0844), static_cast<uint16_t>(8));
    // 没写过 SP 的回路：SP 默认 25.0，PV 从 SP 起步，只有 ±1 噪声
    int pv7 = static_cast<int16_t>(m.holding(1, 0x0607));
    CHECK(pv7 >= 249 && pv7 <= 251);

    // PV 每步最多走 5 个原码 + ±1 噪声，最终贴近 SP
    t.set_setpoint(m, 1, 100.0);
    t.start(m, 1);
    m.set_holding(1, 0x0600, 250);
    m.simulate_step();
    int pv = static_cast<int16_t>(m.holding(1, 0x0600));
    CHECK(pv >= 254 && pv <= 256);
    for (int i = 0; i < 300; ++i) m.simulate_step();
    pv = static_cast<int16_t>(m.holding(1, 0x0600));
    CHECK(std::abs(pv - 1000) <= 3);

    // At=4 → 冷却回 25.0
    t.stop(m, 1);
    for (int i = 0; i < 300; ++i) m.simulate_step();
    pv = static_cast<int16_t>(m.holding(1, 0x0600));
    CHECK(std::abs(pv - 250) <= 3);

    // Srun=9655 全局停止也冷却
    t.start(m, 1);
    for (int i = 0; i < 300; ++i) m.simulate_step();
    t.stop_all(m);
    for (int i = 0; i < 300; ++i) m.simulate_step();
    pv = static_cast<int16_t>(m.holding(1, 0x0600));
    CHECK(std::abs(pv - 250) <= 3);
}

TEST(hw_mock, simulate_autotune_and_pressure) {
    MockModbusMaster::Options o;
    o.temp_slaves = {1};
    o.temp_loops = 8;
    o.analog_slave = 1;
    o.seed = 7;
    MockModbusMaster m(o);
    TempController t(1, 8, 1);
    t.autotune(m, 3);
    for (int i = 0; i < 5; ++i) m.simulate_step();
    CHECK_EQ(m.holding(1, 0x0302), static_cast<uint16_t>(1));  // 5 步还在整定
    m.simulate_step();
    CHECK_EQ(m.holding(1, 0x0302), static_cast<uint16_t>(0));  // 第 6 步回 APID
    int p = m.holding(1, 0x0062), i = m.holding(1, 0x00C2), d = m.holding(1, 0x0122);
    CHECK(p >= 700 && p < 900);
    CHECK(i >= 1000 && i < 1400);
    CHECK(d >= 1500 && d < 2300);

    // 压力：从 8000 起每步 ±30，夹在 3200..16000
    AnalogModule ai(1);
    m.set_input(1, 5, 3210);
    for (int k = 0; k < 50; ++k) m.simulate_step();
    auto raw = ai.read_raw(m);
    CHECK(raw && raw->size() == 8);
    for (auto v : *raw) CHECK(v >= 3200 && v <= 16000);
    int v0 = (*raw)[0];
    CHECK(std::abs(v0 - 8000) <= 30 * 56);
}

TEST(hw_mock, failure_injection_and_bits) {
    MockModbusMaster m;
    m.set_discrete(1, 3, true);
    auto r = m.read_discrete_inputs(1, 0, 16);
    CHECK(r.ok && r.bits.size() == 16 && r.bits[3] && !r.bits[2]);
    m.fail_next(2);
    CHECK(!m.read_holding(1, 0, 1).ok);
    CHECK(!m.read_holding(1, 0, 1).ok);
    CHECK(m.read_holding(1, 0, 1).ok);
    m.set_fail_all(true);
    CHECK(!m.write_register(1, 0, 1).ok);
    m.set_fail_all(false);
    CHECK(!m.read_holding(1, 0, 0).ok);  // 非法请求
    CHECK_EQ(m.txn_count(), static_cast<int64_t>(6));
}

// ===================== 8AI =====================

TEST(hw_analog, scale_4_20_and_0_10) {
    // C# AnalogModule.Scale
    CHECK_NEAR(AnalogModule::scale(3200, 0, 600, true), 0.0, 1e-9);
    CHECK_NEAR(AnalogModule::scale(16000, 0, 600, true), 600.0, 1e-9);
    CHECK_NEAR(AnalogModule::scale(9600, 0, 600, true), 300.0, 1e-9);
    CHECK_NEAR(AnalogModule::scale(0, 0, 600, true), 0.0, 1e-9);        // 低于 4 mA 按 0
    CHECK_NEAR(AnalogModule::scale(17600, 0, 600, true), 675.0, 1e-9);  // 高于满程不夹（同 C#）
    CHECK_NEAR(AnalogModule::scale(8000, 0, 600, false), 300.0, 1e-9);
    CHECK_NEAR(AnalogModule::scale(0, 0, 600, false), 0.0, 1e-9);
    CHECK_NEAR(AnalogModule::to_psi(9600, 600, PressSignal::Current4to20mA), 300.0, 1e-9);
    CHECK_NEAR(AnalogModule::to_psi(4000, 1000, PressSignal::Voltage0to10V), 250.0, 1e-9);

    MockModbusMaster m;
    AnalogModule ai(1);
    for (int i = 0; i < 8; ++i) m.set_input(1, static_cast<uint16_t>(i), static_cast<uint16_t>(3200 + i * 1600));
    auto q = ai.req_read_raw();
    CHECK(q.fc == kFcReadInput && q.addr == 0 && q.count == 8);
    auto psi = ai.decode_psi(m.execute(q), 600, PressSignal::Current4to20mA);
    CHECK(psi && psi->size() == 8);
    CHECK_NEAR((*psi)[0], 0.0, 1e-9);
    CHECK_NEAR((*psi)[4], 300.0, 1e-9);
    CHECK_NEAR((*psi)[7], 525.0, 1e-9);
    auto sc = ai.read_scaled(m, 0, 600, false);
    CHECK_NEAR((*sc)[2], (6400.0 / 16000.0) * 600.0, 1e-9);
    m.fail_next(1);
    CHECK(!ai.read_raw(m).has_value());
}

// ===================== IO16R =====================

TEST(hw_io, valves_polarity_and_frames) {
    MockModbusMaster m;
    IoModule io(1, true);
    IoModule::Valves v{};
    v[1] = true;                // SV2
    v[IoModule::kGasA] = true;  // 气体 A
    m.set_coil(1, 13, true);    // 范围外的线圈
    CHECK(io.write_valves(m, v));
    auto last = m.last_request();
    CHECK(last.fc == kFcWriteCoils && last.addr == 0 && last.count == 12);
    CHECK(m.coil(1, 1) && m.coil(1, 9) && !m.coil(1, 0) && !m.coil(1, 11));
    CHECK(m.coil(1, 13));
    auto st = io.read_open_states(m);
    CHECK(st && (*st)[1] && (*st)[9] && !(*st)[11]);

    // 只关不开：线圈 8..10，其余不碰
    CHECK(io.close_range(m, IoModule::kGasBase, 3));
    last = m.last_request();
    CHECK(last.addr == 8 && last.count == 3);
    CHECK(!m.coil(1, 9) && m.coil(1, 1));
    auto all = io.req_close_all_valves();
    CHECK(all.fc == kFcWriteCoils && all.addr == 0 && all.count == 12);
    for (bool b : all.bits) CHECK(!b);

    // 单路 0x05
    auto s = io.req_set_sv(3, true);
    CHECK(s.fc == kFcWriteCoil && s.addr == 2 && s.bits[0]);
    CHECK(io.req_set_vent(true).addr == 11);
    CHECK(io.req_set_gas(0, true).addr == 8 && io.req_set_gas(2, true).addr == 10);

    // 通电 = 关（常开阀）：电平翻转
    IoModule inv(1, false);
    CHECK(inv.write_valves(m, v));
    CHECK(!m.coil(1, 1) && m.coil(1, 0) && m.coil(1, 11) && !m.coil(1, 9));
    auto st2 = inv.read_open_states(m);
    CHECK(st2 && (*st2)[1] && !(*st2)[0]);
    for (bool b : inv.req_close_all_valves().bits) CHECK(b);
    CHECK(!inv.req_set_sv(1, true).bits[0]);

    m.set_discrete(1, 15, true);
    auto di = io.read_inputs(m);
    CHECK(di && di->size() == 16 && (*di)[15]);
}

// ===================== DM2C =====================

TEST(hw_stepper, velocity_stop_and_params) {
    MockModbusMaster m;
    StepperDriver d(1);
    CHECK(d.run_velocity(m, 895, 1000, 1000));
    auto last = m.last_request();
    CHECK(last.fc == kFcWriteRegisters && last.addr == 0x6200 && last.count == 8);
    CHECK(last.regs == (std::vector<uint16_t>{0x0002, 0, 0, 895, 1000, 1000, 0, 0x0010}));
    CHECK_EQ(m.holding(1, 0x6203), static_cast<uint16_t>(895));
    CHECK(d.stop(m));
    CHECK_EQ(m.holding(1, 0x6002), static_cast<uint16_t>(0x0040));
    CHECK_EQ(d.req_manual_set_zero().regs[0], static_cast<uint16_t>(0x0021));

    auto st = d.req_start(1000, 1000);
    CHECK(st.size() == 2 && st[0].addr == 0x1801 && st[0].regs[0] == 0x1111 && st[1].addr == 0x6200);

    // 峰值电流硬夹 0.3–3.2 A + 存 EEPROM
    m.clear_writes();
    CHECK_NEAR(*d.set_peak_current(m, 5.0), 3.2, 1e-9);
    auto w = m.writes();
    CHECK(w.size() == 2 && w[0].addr == 0x0191 && w[0].regs[0] == 32 && w[1].addr == 0x1801 && w[1].regs[0] == 0x2211);
    CHECK_NEAR(*d.set_peak_current(m, 0.1), 0.3, 1e-9);
    CHECK_NEAR(*d.set_peak_current(m, 1.25), 1.2, 1e-9);  // 12.5 → 五成双 12
    CHECK_NEAR(*d.read_peak_current(m), 1.2, 1e-9);
    CHECK_EQ(*d.set_microstep(m, 100), 200);
    CHECK_EQ(*d.set_microstep(m, 60000), 51200);
    CHECK_EQ(*d.read_microstep(m), 51200);
    CHECK_EQ(*d.set_standby_pct(m, 150), 100);
    CHECK_EQ(*d.set_standby_pct(m, -5), 0);
    CHECK_EQ(m.holding(1, 0x01D3), static_cast<uint16_t>(0));
    m.fail_next(1);
    CHECK(!d.set_microstep(m, 400).has_value());

    m.set_holding(1, 0x0177, 240);
    CHECK_NEAR(*d.read_bus_voltage(m), 24.0, 1e-9);
    m.set_holding(1, 0x2203, 0x0031);
    CHECK_EQ(*d.read_alarm(m), 0x31);
    CHECK(d.reset_alarm(m) && m.holding(1, 0x1801) == 0x1111);
    CHECK(d.save(m) && m.holding(1, 0x1801) == 0x2211);
    CHECK_EQ(d.req_jog_forward().regs[0], static_cast<uint16_t>(0x4001));
    CHECK_EQ(d.req_set_jog_speed(300).addr, static_cast<uint16_t>(0x01E1));
}

TEST(hw_stepper, status_bits) {
    MockModbusMaster m;
    StepperDriver d(1);
    m.set_holding(1, 0x1003, 0x0007);
    auto s = d.read_status(m);
    CHECK(s && s->fault() && s->enabled() && s->running() && !s->command_done());
    m.set_holding(1, 0x1003, 0x0070);
    s = d.read_status(m);
    CHECK(s && !s->fault() && s->command_done() && s->path_done() && s->home_done());
}

// C#: StirGearTests
TEST(hw_stepper, stir_gear) {
    CHECK_NEAR(stir_gear::kDefaultRatio, 2.236842, 5e-7);
    CHECK_EQ(stir_gear::to_motor(447, stir_gear::kDefaultRatio, 3000), 1000);  // 釜 447 rpm → 电机约 1000 rpm
    CHECK_EQ(stir_gear::to_motor(400, stir_gear::kDefaultRatio, 3000), 895);
    CHECK_EQ(stir_gear::to_motor(0, stir_gear::kDefaultRatio, 3000), 0);
    CHECK_EQ(stir_gear::to_motor(2000, stir_gear::kDefaultRatio, 3000), 3000);  // 超过电机上限：限幅
    CHECK_NEAR(stir_gear::to_vessel(1000, stir_gear::kDefaultRatio), 447.06, 5e-3);
    CHECK_NEAR(stir_gear::to_vessel(300, 0), 300.0, 1e-6);  // 传动比无效按 1
    CHECK_EQ(stir_gear::to_motor(100, 0, 3000), 100);
    CHECK_EQ(stir_gear::to_motor(-5, 1, 3000), 0);
    CHECK_EQ(stir_gear::kMaxMotorRpm, 3000);
}

// 气路模型（reactord --sim）：常压读 0 psi（原码 ≈ 3200），开气源 + SV 才升压，开排空 + SV 泄到 0
TEST(hw_mock, gas_model_idle_zero_fill_and_vent) {
    MockModbusMaster::Options o;
    o.temp_slaves = {1};
    o.temp_loops = 8;
    o.analog_slave = 1;
    o.gas_model = true;
    o.io_slave = 1;
    o.seed = 3;
    MockModbusMaster m(o);
    AnalogModule ai(1);
    for (int k = 0; k < 5; ++k) m.simulate_step();
    auto raw = ai.read_raw(m);
    CHECK(raw && raw->size() == 8);
    for (auto v : *raw) CHECK(std::abs(static_cast<int>(v) - 3200) <= 1);
    // 开惰性气体（线圈 8）+ SV2（线圈 1）90 s：2 号升到减压阀压力附近，其他路不动
    m.set_coil(1, 8, true);
    m.set_coil(1, 1, true);
    m.simulate_gas(90);
    auto psi = ai.decode_psi(m.read_input(1, 0, 8), 600, PressSignal::Current4to20mA);
    CHECK(psi && (*psi)[1] > 470 && (*psi)[1] < 481);
    CHECK((*psi)[0] < 0.5);
    // 全关：保持
    m.set_coil(1, 8, false);
    m.set_coil(1, 1, false);
    m.simulate_gas(30);
    psi = ai.decode_psi(m.read_input(1, 0, 8), 600, PressSignal::Current4to20mA);
    CHECK(psi && (*psi)[1] > 470);
    // 排空 + SV2：泄到 0
    m.set_coil(1, 11, true);
    m.set_coil(1, 1, true);
    m.simulate_gas(120);
    psi = ai.decode_psi(m.read_input(1, 0, 8), 600, PressSignal::Current4to20mA);
    CHECK(psi && (*psi)[1] < 0.5);
}
