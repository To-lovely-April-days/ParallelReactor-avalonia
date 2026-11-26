#include "ctl/ctl_temp_service.hpp"

namespace reactor::ctl {

TempService::TempService(const CoreIo& io, const store::SettingsState* settings) : io_(io), settings_(settings) {}

int TempService::band_index_for(double t) const {
    auto bands = store::band_ranges(settings_->band_bounds);
    for (int i = 0; i < static_cast<int>(bands.size()); ++i)
        if (t >= bands[static_cast<size_t>(i)].lo && t < bands[static_cast<size_t>(i)].hi) return i;
    return static_cast<int>(bands.size()) - 1;
}

std::vector<hw::ModbusRequest> TempService::pid_reqs(int id, int band) const {
    const auto& p = settings_->band_pids[static_cast<size_t>(id - 1)][static_cast<size_t>(band)];
    return io_.hw->temp.req_write_pid(id, p.p, p.i, p.d);
}

void TempService::init(bool seed_from_instrument, std::function<void(int, const hw::PidParams&)> seed) {
    auto& t = io_.hw->temp;
    auto& bus = *io_.hw->temp_bus;
    // C# InitAsync：读 Ctn → <8 写 8 再回读；然后 8 路 At=4（遇失败即停）；最后（首次运行）读 PID 作种子
    auto after_ctn = [this, seed_from_instrument, seed]() {
        auto& tc = io_.hw->temp;
        io_.chain(*io_.hw->temp_bus, tc.stop_all_loops_requests(), /*stop_on_fail=*/true, 0,
                  [this, seed_from_instrument, seed](const std::vector<hw::ModbusResult>& rs, bool) {
                      for (size_t i = 0; i < rs.size(); ++i)
                          if (rs[i].ok) ch_[i].mode = hw::TempController::kAtStop;
                      if (!seed_from_instrument) return;
                      for (int id = 1; id <= kChannels; ++id) {
                          io_.chain(*io_.hw->temp_bus, io_.hw->temp.req_read_pid(id), true, 0,
                                    [this, id, seed](const std::vector<hw::ModbusResult>& prs, bool) {
                                        if (auto p = io_.hw->temp.decode_pid(prs); p && seed) seed(id, *p);
                                    });
                      }
                  });
    };
    io_.submit(bus, t.req_read_ctn(), [this, after_ctn](const hw::ModbusResult& r) {
        auto c = hw::TempController::decode_u16(r);
        if (c && *c >= 0 && *c < 8) {
            auto& tc = io_.hw->temp;
            io_.chain(*io_.hw->temp_bus, {tc.req_write_ctn(8), tc.req_read_ctn()}, true, 0,
                      [this, after_ctn](const std::vector<hw::ModbusResult>& rs, bool) {
                          if (rs.size() == 2)
                              if (auto v = hw::TempController::decode_u16(rs[1])) ctn_ = *v;
                          after_ctn();
                      });
            return;
        }
        if (c) ctn_ = *c;
        after_ctn();
    });
}

void TempService::poll() {
    if (polling_) return;  // C# _polling：上一轮还没读完
    polling_ = true;
    io_.chain(*io_.hw->temp_bus, io_.hw->temp.poll_requests(), true, 0,
              [this](const std::vector<hw::ModbusResult>& rs, bool) {
                  polling_ = false;
                  auto p = io_.hw->temp.decode_poll(rs);
                  if (!p) {
                      stats_.fail();
                      if (!stats_.online())
                          for (auto& c : ch_) c.online = false;
                      return;
                  }
                  int64_t at = io_.wall();
                  stats_.ok(at);
                  values_at_ = at;
                  srun_ = p->srun;
                  ctn_ = p->ctn;
                  for (size_t i = 0; i < ch_.size() && i < p->pv.size(); ++i) {
                      auto& m = ch_[i];
                      bool tuning = p->at[i] == hw::TempController::kAtAutotune;
                      bool finished = m.autotuning && !tuning;
                      m.pv_raw = p->pv_raw[i];
                      m.mode = p->at[i];
                      m.pv = p->pv[i];
                      m.output_pct = p->op_pct[i];
                      m.autotuning = tuning;
                      m.online = true;
                      if (finished && on_autotune_finished) on_autotune_finished(static_cast<int>(i) + 1);
                  }
              });
}

void TempService::start_channel(int id, double band_temp, double initial_sp, uint64_t guard_epoch, DoneFn cb) {
    auto& t = io_.hw->temp;
    int band = band_index_for(band_temp);
    int gen = pid_gen_[static_cast<size_t>(id - 1)];
    std::vector<hw::ModbusRequest> reqs{t.req_run_all(), t.req_start(id)};
    for (auto& r : pid_reqs(id, band)) reqs.push_back(r);
    reqs.push_back(t.req_write_sp(id, initial_sp));
    last_band_[static_cast<size_t>(id - 1)] = -1;
    io_.chain(*io_.hw->temp_bus, std::move(reqs), true, guard_epoch,
              [this, id, band, gen, initial_sp, cb](const std::vector<hw::ModbusResult>& rs, bool aborted) {
                  bool ok = !aborted && rs.size() == 6;
                  for (const auto& r : rs) ok = ok && r.ok;
                  size_t i = static_cast<size_t>(id - 1);
                  if (rs.size() >= 5) {  // PID 三笔都做完了
                      bool pid_ok = rs[2].ok && rs[3].ok && rs[4].ok;
                      last_band_[i] = pid_ok && pid_gen_[i] == gen ? band : -1;
                  }
                  if (ok) {
                      ch_[i].sp = initial_sp;
                      ch_[i].sp_known = true;
                  }
                  if (cb) cb(ok);
              });
}

void TempService::write_sp_safe(int id, double sp, double band_temp, DoneFn cb) {
    size_t i = static_cast<size_t>(id - 1);
    int band = band_index_for(band_temp);
    std::vector<hw::ModbusRequest> reqs;
    bool with_pid = last_band_[i] != band;
    int gen = pid_gen_[i];
    if (with_pid) reqs = pid_reqs(id, band);
    reqs.push_back(io_.hw->temp.req_write_sp(id, sp));
    io_.chain(*io_.hw->temp_bus, std::move(reqs), /*stop_on_fail=*/false, 0,
              [this, i, band, gen, with_pid, sp, cb](const std::vector<hw::ModbusResult>& rs, bool) {
                  if (with_pid) {
                      bool pid_ok = rs.size() >= 3 && rs[0].ok && rs[1].ok && rs[2].ok;
                      last_band_[i] = pid_ok && pid_gen_[i] == gen ? band : -1;
                  }
                  bool ok = !rs.empty() && rs.back().ok && rs.size() == (with_pid ? 4u : 1u);
                  if (ok) {
                      ch_[i].sp = sp;
                      ch_[i].sp_known = true;
                  }
                  if (cb) cb(ok);
              });
}

void TempService::set_setpoint(int id, double sp, DoneFn cb) {
    io_.submit(*io_.hw->temp_bus, io_.hw->temp.req_write_sp(id, sp), [this, id, sp, cb](const hw::ModbusResult& r) {
        if (r.ok) {
            ch_[static_cast<size_t>(id - 1)].sp = sp;
            ch_[static_cast<size_t>(id - 1)].sp_known = true;
        }
        if (cb) cb(r.ok);
    });
}

void TempService::stop_channel(int id, DoneFn cb) {
    io_.submit(*io_.hw->temp_bus, io_.hw->temp.req_stop(id), [cb](const hw::ModbusResult& r) {
        if (cb) cb(r.ok);
    });
}

void TempService::over_temp_stop(int id, DoneFn cb) {
    size_t i = static_cast<size_t>(id - 1);
    bool was_tuning = ch_[i].autotuning;
    io_.submit(*io_.hw->temp_bus, io_.hw->temp.req_stop(id), [this, i, id, was_tuning, cb](const hw::ModbusResult& r) {
        if (r.ok) {
            // C# OverTempStopAsync：写入确认后解除「整定中」；整定被打断 → 第二阶段的整定簿据此不回读存档
            if (was_tuning && on_autotune_aborted) on_autotune_aborted(id);
            ch_[i].autotuning = false;
        }
        if (cb) cb(r.ok);
    });
}

void TempService::read_setpoint(int id, std::function<void(std::optional<double>)> cb) {
    io_.submit(*io_.hw->temp_bus, io_.hw->temp.req_read_sp(id), [this, cb](const hw::ModbusResult& r) {
        if (cb) cb(io_.hw->temp.decode_temp(r));
    });
}

void TempService::start_autotune(int id, uint64_t guard_epoch, DoneFn cb) {
    auto& t = io_.hw->temp;
    io_.chain(*io_.hw->temp_bus, {t.req_run_all(), t.req_autotune(id)}, true, guard_epoch,
              [this, id, cb](const std::vector<hw::ModbusResult>& rs, bool aborted) {
                  bool ok = !aborted && rs.size() == 2 && rs[0].ok && rs[1].ok;
                  if (ok) {
                      size_t i = static_cast<size_t>(id - 1);
                      last_band_[i] = -1;  // 整定会改写仪表 PID，完成回读后再确定
                      ch_[i].autotuning = true;
                      ch_[i].mode = hw::TempController::kAtAutotune;
                  }
                  if (cb) cb(ok);
              });
}

void TempService::read_pid(int id, std::function<void(std::optional<hw::PidParams>)> cb) {
    io_.chain(*io_.hw->temp_bus, io_.hw->temp.req_read_pid(id), true, 0,
              [this, cb](const std::vector<hw::ModbusResult>& rs, bool) {
                  if (cb) cb(io_.hw->temp.decode_pid(rs));
              });
}

}  // namespace reactor::ctl
