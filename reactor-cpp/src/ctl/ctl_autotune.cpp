#include "ctl/ctl_autotune.hpp"
#include "core/chan_name.hpp"

#include <cmath>
#include <memory>

#include "core/num_format.hpp"
#include "core/value_rules.hpp"
#include "ctl/ctl_gas_ops.hpp"
#include "store/settings.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::ctl {

using core::ReactorState;

namespace {
std::string rvs(int ch) { return "RV" + std::to_string(ch); }
// C# OperatorCommands.Temp.Fmt(v, "℃")：v.ToString("0.###") + " ℃"
std::string fmt_c(double v) { return core::fmt_dec(v, 3) + " ℃"; }
}  // namespace

Autotune::Autotune(ControlCore& core, core::PressureUnit unit) : core_(core), unit_(unit) {
    core_.temp_mut().on_autotune_finished = [this](int id) { on_finished(id); };
    core_.temp_mut().on_autotune_aborted = [this](int id) { on_aborted(id); };
}

Autotune::~Autotune() {
    core_.temp_mut().on_autotune_finished = nullptr;
    core_.temp_mut().on_autotune_aborted = nullptr;
}

std::string Autotune::band_name(int band) {
    static const char* kNames[3] = {"低温档", "中温档", "高温档"};  // C# TempService.Bands
    return band >= 0 && band < 3 ? kNames[band] : "档位 " + std::to_string(band);
}

OpResult Autotune::check(int ch) const {
    if (ch < 1 || ch > 8) return OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(ch) + " 不存在（有效范围 1–8）");
    if (auto hb = gas_ops::heat_block(core_, ch, unit_)) return OpResult::fail(hb->code, hb->message);
    return OpResult::none();
}

void Autotune::start(int ch, double temp_c, std::optional<int> band, bool strict, std::function<void(const OpResult&)> done) {
    auto reply = [done](const OpResult& r) { if (done) done(r); };
    if (ch < 1 || ch > 8) { reply(OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(ch) + " 不存在（有效范围 1–8）")); return; }
    if (auto hb = gas_ops::heat_block(core_, ch, unit_)) { reply(OpResult::fail(hb->code, hb->message)); return; }
    const auto& s = core_.settings();
    if (auto bad = core::value_rules::autotune_sp(temp_c, s.settings.t_max_sp)) { reply(OpResult::invalid(*bad)); return; }
    if (band) {
        std::vector<std::pair<double, double>> bands;
        for (const auto& b : store::band_ranges(s.band_bounds)) bands.emplace_back(b.lo, b.hi);
        if (auto bad = core::value_rules::autotune_band(*band, temp_c, bands, s.settings.t_max_sp)) { reply(OpResult::invalid(*bad)); return; }
    }
    int tune_band = band.value_or(core_.temp().band_index_for(temp_c));  // 结果存入的档由目标温度决定（不受回读 SP 的小数舍入影响）
    if (strict) {
        if (core_.temp().ch(ch).autotuning) { reply(OpResult::fail(core::op_codes::kAutotuneActive, core::chan_name(ch) + " 正在自整定 · 请先取消")); return; }
        auto st = core_.channel(ch).state;
        if (st != ReactorState::Idle && st != ReactorState::Ready && st != ReactorState::Done) {
            reply(OpResult::fail(core::op_codes::kChannelStateInvalid, core::chan_name(ch) + " 正在运行，不能自整定"));
            return;
        }
    }
    double sp = temp_c;
    // 1) 先把 SP 写成整定目标温度
    core_.temp_mut().set_setpoint(ch, sp, [this, ch, sp, tune_band, reply](bool ok) {
        if (!ok) {
            reply(OpResult::fail(core::op_codes::kHardwareWriteFailed, "SP 下发失败，未启动整定", "err",
                                 ojson{{"device", "temp"}, {"channel", ch}, {"error", "温控通讯失败"}}));
            return;
        }
        auto finish = [this, ch, sp, tune_band, reply](std::optional<std::string> err) {
            core_.alarms().log(err ? "warn" : "info", rvs(ch), err ? "自整定未启动" : "启动自整定",
                               err ? *err : "整定目标 " + fmt_c(sp) + " · 结果存入" + band_name(tune_band),
                               err ? "temp.autotuneRejected" : "temp.autotuneStarted");
            if (err) {
                reply(OpResult::fail(core::op_codes::kHardwareWriteFailed, *err, "err", ojson{{"device", "temp"}, {"channel", ch}}));
                return;
            }
            reply(OpResult::done(core::chan_name(ch) + " 已设目标 " + core::fmt_dec(sp, 0) + "℃ 并启动自整定", "ok",
                                 ojson{{"channel", ch}, {"sp", store::json_num(sp)}, {"band", tune_band}}));
        };
        // 2) C# TempService.AutotuneAsync：回读 SP 把关（≈0 ℃ 时振荡不起来）→ Srun=0 → At=1
        core_.temp_mut().read_setpoint(ch, [this, ch, tune_band, finish](std::optional<double> rsp) {
            if (!rsp) { finish(std::string("通讯失败，未能启动整定")); return; }
            if (*rsp < kMinSp) { finish(std::string(kNoSetpoint)); return; }
            auto& b = book_[idx(ch)];
            b.aborted = false;  // ClearAbort
            double read_sp = *rsp;
            core_.start_autotune_chain(ch, [this, ch, tune_band, read_sp, finish](bool ok) {
                if (!ok) { finish(std::string("通讯失败，未能启动整定")); return; }
                auto& bk = book_[idx(ch)];
                bk.band = tune_band >= 0 && tune_band < store::kBandCount ? tune_band : core_.temp().band_index_for(read_sp);
                bk.sp = read_sp;
                bk.state = "tuning";
                bk.shown_band = bk.band;
                bk.shown_sp = read_sp;
                bk.started_at = core_.clock().wall_ms();
                bk.started_mono = core_.clock().mono_ms();
                bk.finished_at.reset();
                bk.result.reset();
                bk.message = "整定中 · 目标 " + fmt_c(read_sp) + " · 结果存入" + band_name(bk.band);
                finish(std::nullopt);
            });
        });
    });
}

void Autotune::cancel(int ch, std::function<void(const OpResult&)> done) {
    if (ch < 1 || ch > 8) {
        if (done) done(OpResult::fail(core::op_codes::kChannelNotFound, "通道 " + std::to_string(ch) + " 不存在（有效范围 1–8）"));
        return;
    }
    on_aborted(ch);
    bool was = book_[idx(ch)].state == "aborted";
    if (was) book_[idx(ch)].message = "已取消";
    // 取消 = 停下来：At=4（不写回 0，否则仪表带着 SP 继续出力）；通讯失败也要把界面状态解开
    core_.temp_mut().stop_channel(ch, [this, ch, done](bool) {
        core_.temp_mut().ch_mut(ch).autotuning = false;
        core_.alarms().log("info", rvs(ch), "取消自整定", "At=4 停止输出", "temp.autotuneCancelled");
        if (done) done(OpResult::done(core::chan_name(ch) + " 已取消自整定并停止输出"));
    });
}

// AutotuneBook.Abort：之后轮询看到「整定结束」时不回读、不存档、不记来历
void Autotune::on_aborted(int ch) {
    auto& b = book_[idx(ch)];
    b.aborted = true;
    b.band = -1;
    b.sp.reset();
    if (b.state == "tuning") {
        b.state = "aborted";
        b.finished_at = core_.clock().wall_ms();
        b.message = "整定被打断 · 不回读";
    }
}

// TempService 轮询看到 At 从 1 变成非 1（C# Poll → Tune.OnFinished → ReloadPidAsync → Tune.Complete）
void Autotune::on_finished(int ch) {
    auto& b = book_[idx(ch)];
    if (b.aborted) {  // 被打断：清掉标记，什么都不记
        b.aborted = false;
        return;
    }
    if (b.state.empty() || b.state == "done" || b.state == "failed" || b.state == "aborted") {
        // 不是软件发起的整定（仪表自己发起）：照样回读，存入当前目标温度所在档，目标温度未知
        b.state = "tuning";
        b.started_at = core_.clock().wall_ms();
        b.started_mono = core_.clock().mono_ms();
    }
    b.state = "reading";
    core_.temp_mut().read_pid(ch, [this, ch](std::optional<hw::PidParams> pid) {
        auto& bk = book_[idx(ch)];
        if (!pid) {
            bk.state = "failed";
            bk.finished_at = core_.clock().wall_ms();
            bk.message = "整定完成但回读 PID 失败（温控通讯）";
            core_.alarms().log("warn", rvs(ch), "自整定结果未回读", "AI-8 通讯失败，整定出的 PID 没有存档", "temp.autotuneReadFailed");
            return;
        }
        // AutotuneBook.Complete：存回发起时的档（没有则按当前 SP 所在档），来历 = {目标温度, 完成时刻}
        int fallback = core_.temp().band_index_for(core_.temp().ch(ch).sp);
        int band = bk.band >= 0 ? bk.band : fallback;
        std::optional<double> sp = bk.band >= 0 ? bk.sp : std::nullopt;
        bk.band = -1;
        bk.sp.reset();
        int64_t now = core_.clock().wall_ms();
        store::Pid p{pid->p, pid->i, pid->d};
        if (band >= 0 && band < store::kBandCount)
            core_.update_settings([ch, band, sp, now, p](store::SettingsState& st) {
                st.band_pids[static_cast<size_t>(ch - 1)][static_cast<size_t>(band)] = p;
                st.has_band_pids = true;
                store::tune_complete(st, ch - 1, band, sp, now);
            });
        core_.temp_mut().bump_pid_gen(ch);
        core_.temp_mut().forget_live_band(ch);  // 下一次写 SP 时按档重写（同值，保证仪表与存档一致）
        bk.state = "done";
        bk.shown_band = band;
        bk.finished_at = now;
        bk.result = p;
        bk.message = "整定完成 · 已存入" + band_name(band);
        core_.alarms().log("info", rvs(ch), "自整定完成",
                           "结果存入" + band_name(band) + " · P " + core::fmt_dec(p.p, 1) + " · I " + core::fmt_dec(p.i, 1) + " s · D " +
                               core::fmt_dec(p.d, 2) + " s",
                           "temp.autotuneDone");
    });
}

ojson Autotune::snapshot() const {
    ojson arr = ojson::array();
    for (int ch = 1; ch <= 8; ++ch) {
        const auto& b = book_[idx(ch)];
        if (b.state.empty()) continue;
        ojson o;
        o["channel"] = ch;
        o["state"] = b.state;
        o["running"] = b.state == "tuning" || b.state == "reading";
        o["band"] = b.shown_band;
        o["bandName"] = band_name(b.shown_band);
        o["spC"] = b.shown_sp ? ojson(store::json_num(*b.shown_sp)) : ojson(nullptr);
        o["startedAt"] = b.started_at ? ojson(store::fmt_utc_json(*b.started_at)) : ojson(nullptr);
        o["finishedAt"] = b.finished_at ? ojson(store::fmt_utc_json(*b.finished_at)) : ojson(nullptr);
        o["elapsedSec"] = o["running"].get<bool>() ? ojson((core_.clock().mono_ms() - b.started_mono) / 1000) : ojson(nullptr);
        o["result"] = b.result ? ojson{{"p", store::json_num(b.result->p)}, {"i", store::json_num(b.result->i)}, {"d", store::json_num(b.result->d)}}
                               : ojson(nullptr);
        o["message"] = b.message;
        arr.push_back(o);
    }
    if (arr.empty()) return nullptr;
    return ojson{{"channels", arr}};
}

}  // namespace reactor::ctl
