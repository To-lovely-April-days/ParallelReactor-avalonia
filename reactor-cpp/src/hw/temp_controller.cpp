#include "hw/temp_controller.hpp"

#include <cmath>

namespace reactor::hw {

TempController::TempController(uint8_t slave, int loops, int dpt)
    : slave_(slave), loops_(loops), dpt_(dpt), scale_(std::pow(10.0, dpt)) {}

ModbusRequest TempController::block(uint16_t base) const {
    return req_read_holding(slave_, base, static_cast<uint16_t>(loops_));
}

ModbusRequest TempController::req_read_pv_block() const { return block(kPvBase); }
ModbusRequest TempController::req_read_op_block() const { return block(kOpBase); }
ModbusRequest TempController::req_read_at_block() const { return block(kAtBase); }
ModbusRequest TempController::req_read_sv_block() const { return block(kSvBase); }
ModbusRequest TempController::req_read_sp_block() const { return block(kSpBase); }
ModbusRequest TempController::req_read_pv(int loop) const { return req_read_holding(slave_, at_loop(kPvBase, loop), 1); }
ModbusRequest TempController::req_read_sp(int loop) const { return req_read_holding(slave_, at_loop(kSpBase, loop), 1); }
ModbusRequest TempController::req_read_sv(int loop) const { return req_read_holding(slave_, at_loop(kSvBase, loop), 1); }
ModbusRequest TempController::req_read_op(int loop) const { return req_read_holding(slave_, at_loop(kOpBase, loop), 1); }
ModbusRequest TempController::req_read_at(int loop) const { return req_read_holding(slave_, at_loop(kAtBase, loop), 1); }
ModbusRequest TempController::req_read_srun() const { return req_read_holding(slave_, kSrunReg, 1); }
ModbusRequest TempController::req_read_ctn() const { return req_read_holding(slave_, kCtnReg, 1); }
ModbusRequest TempController::req_write_ctn(int n) const { return req_write_register(slave_, kCtnReg, reg_from_u16(n)); }
ModbusRequest TempController::req_write_srun(int v) const { return req_write_register(slave_, kSrunReg, reg_from_u16(v)); }
ModbusRequest TempController::req_write_sp(int loop, double c) const {
    return req_write_register(slave_, at_loop(kSpBase, loop), temp_to_reg(c));
}
ModbusRequest TempController::req_write_at(int loop, int mode) const {
    return req_write_register(slave_, at_loop(kAtBase, loop), reg_from_u16(mode));
}
ModbusRequest TempController::req_read_p(int loop) const { return req_read_holding(slave_, at_loop(kPBase, loop), 1); }
ModbusRequest TempController::req_read_i(int loop) const { return req_read_holding(slave_, at_loop(kIBase, loop), 1); }
ModbusRequest TempController::req_read_d(int loop) const { return req_read_holding(slave_, at_loop(kDBase, loop), 1); }
ModbusRequest TempController::req_write_p(int loop, double p) const {
    return req_write_register(slave_, at_loop(kPBase, loop), reg_from_u16(p * scale_));
}
ModbusRequest TempController::req_write_i(int loop, double s) const {
    return req_write_register(slave_, at_loop(kIBase, loop), reg_from_u16(s * 10.0));
}
ModbusRequest TempController::req_write_d(int loop, double s) const {
    return req_write_register(slave_, at_loop(kDBase, loop), reg_from_i16(s * 100.0));
}
std::vector<ModbusRequest> TempController::req_write_pid(int loop, double p, double i, double d) const {
    return {req_write_p(loop, p), req_write_i(loop, i), req_write_d(loop, d)};
}
std::vector<ModbusRequest> TempController::req_read_pid(int loop) const {
    return {req_read_p(loop), req_read_i(loop), req_read_d(loop)};
}
std::vector<ModbusRequest> TempController::poll_requests() const {
    return {req_read_pv_block(), req_read_op_block(), req_read_at_block(), req_read_srun(), req_read_ctn()};
}
std::vector<ModbusRequest> TempController::stop_all_loops_requests() const {
    std::vector<ModbusRequest> v;
    for (int loop = 1; loop <= loops_; ++loop) v.push_back(req_stop(loop));
    return v;
}

// ---- 解码 ----

static bool has(const ModbusResult& r, size_t n) { return r.ok && r.regs.size() == n; }

std::optional<std::vector<int16_t>> TempController::decode_raw_block(const ModbusResult& r) const {
    if (!has(r, static_cast<size_t>(loops_))) return std::nullopt;
    std::vector<int16_t> v(r.regs.size());
    for (size_t i = 0; i < v.size(); ++i) v[i] = static_cast<int16_t>(r.regs[i]);
    return v;
}
std::optional<std::vector<double>> TempController::decode_temp_block(const ModbusResult& r) const {
    if (!has(r, static_cast<size_t>(loops_))) return std::nullopt;
    std::vector<double> v(r.regs.size());
    for (size_t i = 0; i < v.size(); ++i) v[i] = from_raw(static_cast<int16_t>(r.regs[i]));
    return v;
}
std::optional<std::vector<double>> TempController::decode_output_block(const ModbusResult& r) const {
    if (!has(r, static_cast<size_t>(loops_))) return std::nullopt;
    std::vector<double> v(r.regs.size());
    for (size_t i = 0; i < v.size(); ++i) v[i] = r.regs[i] / kOutputFull;
    return v;
}
std::optional<std::vector<int>> TempController::decode_mode_block(const ModbusResult& r) const {
    if (!has(r, static_cast<size_t>(loops_))) return std::nullopt;
    return std::vector<int>(r.regs.begin(), r.regs.end());
}
std::optional<double> TempController::decode_temp(const ModbusResult& r) const {
    if (!has(r, 1)) return std::nullopt;
    return from_raw(static_cast<int16_t>(r.regs[0]));
}
std::optional<double> TempController::decode_output(const ModbusResult& r) const {
    if (!has(r, 1)) return std::nullopt;
    return r.regs[0] / kOutputFull;
}
std::optional<double> TempController::decode_i(const ModbusResult& r) {
    if (!has(r, 1)) return std::nullopt;
    return r.regs[0] / 10.0;
}
std::optional<double> TempController::decode_d(const ModbusResult& r) {
    if (!has(r, 1)) return std::nullopt;
    return static_cast<int16_t>(r.regs[0]) / 100.0;
}
std::optional<int> TempController::decode_u16(const ModbusResult& r) {
    if (!has(r, 1)) return std::nullopt;
    return static_cast<int>(r.regs[0]);
}

std::optional<TempPoll> TempController::decode_poll(const std::vector<ModbusResult>& rs) const {
    if (rs.size() != 5) return std::nullopt;
    auto raw = decode_raw_block(rs[0]);
    auto op = decode_output_block(rs[1]);
    auto at = decode_mode_block(rs[2]);
    auto srun = decode_u16(rs[3]);
    auto ctn = decode_u16(rs[4]);
    if (!raw || !op || !at || !srun || !ctn) return std::nullopt;
    TempPoll p;
    p.pv_raw = *raw;
    p.pv.resize(raw->size());
    for (size_t i = 0; i < raw->size(); ++i) p.pv[i] = from_raw((*raw)[i]);
    p.op_pct = *op;
    p.at = *at;
    p.srun = *srun;
    p.ctn = *ctn;
    return p;
}

std::optional<PidParams> TempController::decode_pid(const std::vector<ModbusResult>& rs) const {
    if (rs.size() != 3) return std::nullopt;
    auto p = decode_temp(rs[0]);
    auto i = decode_i(rs[1]);
    auto d = decode_d(rs[2]);
    if (!p || !i || !d) return std::nullopt;
    return PidParams{*p, *i, *d};
}

// ---- 同步 ----

std::optional<std::vector<double>> TempController::read_all_pv(ModbusMaster& m) const { return decode_temp_block(m.execute(req_read_pv_block())); }
std::optional<std::vector<int16_t>> TempController::read_all_pv_raw(ModbusMaster& m) const { return decode_raw_block(m.execute(req_read_pv_block())); }
std::optional<std::vector<double>> TempController::read_all_outputs(ModbusMaster& m) const { return decode_output_block(m.execute(req_read_op_block())); }
std::optional<std::vector<int>> TempController::read_all_modes(ModbusMaster& m) const { return decode_mode_block(m.execute(req_read_at_block())); }
std::optional<std::vector<double>> TempController::read_all_sv(ModbusMaster& m) const { return decode_temp_block(m.execute(req_read_sv_block())); }
std::optional<TempPoll> TempController::poll(ModbusMaster& m) const { return decode_poll(execute_seq(m, poll_requests())); }
std::optional<double> TempController::read_pv(ModbusMaster& m, int loop) const { return decode_temp(m.execute(req_read_pv(loop))); }
std::optional<double> TempController::read_setpoint(ModbusMaster& m, int loop) const { return decode_temp(m.execute(req_read_sp(loop))); }
std::optional<double> TempController::read_sv(ModbusMaster& m, int loop) const { return decode_temp(m.execute(req_read_sv(loop))); }
std::optional<double> TempController::read_output(ModbusMaster& m, int loop) const { return decode_output(m.execute(req_read_op(loop))); }
std::optional<int> TempController::read_mode(ModbusMaster& m, int loop) const { return decode_u16(m.execute(req_read_at(loop))); }
std::optional<int> TempController::read_srun(ModbusMaster& m) const { return decode_u16(m.execute(req_read_srun())); }
std::optional<int> TempController::read_ctn(ModbusMaster& m) const { return decode_u16(m.execute(req_read_ctn())); }
std::optional<PidParams> TempController::read_pid(ModbusMaster& m, int loop) const { return decode_pid(execute_seq(m, req_read_pid(loop))); }
bool TempController::set_setpoint(ModbusMaster& m, int loop, double c) const { return m.execute(req_write_sp(loop, c)).ok; }
bool TempController::set_ctn(ModbusMaster& m, int n) const { return m.execute(req_write_ctn(n)).ok; }
bool TempController::start(ModbusMaster& m, int loop) const { return m.execute(req_start(loop)).ok; }
bool TempController::stop(ModbusMaster& m, int loop) const { return m.execute(req_stop(loop)).ok; }
bool TempController::autotune(ModbusMaster& m, int loop) const { return m.execute(req_autotune(loop)).ok; }
bool TempController::stop_all(ModbusMaster& m) const { return m.execute(req_stop_all()).ok; }
bool TempController::run_all(ModbusMaster& m) const { return m.execute(req_run_all()).ok; }
bool TempController::set_pid(ModbusMaster& m, int loop, double p, double i, double d) const {
    auto rs = execute_seq(m, req_write_pid(loop, p, i, d));
    return rs.size() == 3 && rs.back().ok;
}

}  // namespace reactor::hw
