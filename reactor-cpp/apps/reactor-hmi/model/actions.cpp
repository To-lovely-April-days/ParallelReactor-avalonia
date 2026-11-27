#include "actions.hpp"

#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hmi::model {

#define LOGA(...) (std::printf("[action] " __VA_ARGS__), std::printf("\n"), std::fflush(stdout))
#define FWD(call) \
    do {          \
        if (fwd_) fwd_->call; \
    } while (0)

void LoggingActions::stop_all(bool s) { LOGA("stop_all include_stir=%d", s); FWD(stop_all(s)); }
void LoggingActions::stir_set_rpm(int r) { LOGA("stir_set_rpm %d", r); FWD(stir_set_rpm(r)); }
void LoggingActions::stir_run(bool on) { LOGA("stir_run %d", on); FWD(stir_run(on)); }
void LoggingActions::manual_valve(bool on) { LOGA("manual_valve %d", on); FWD(manual_valve(on)); }
void LoggingActions::set_temp_sp(int ch, double c) { LOGA("set_temp_sp ch%d %.1f", ch, c); FWD(set_temp_sp(ch, c)); }
void LoggingActions::heat(int ch, bool on) { LOGA("heat ch%d %d", ch, on); FWD(heat(ch, on)); }
void LoggingActions::set_heat_mode(int ch, const std::string& m) { LOGA("set_heat_mode ch%d %s", ch, m.c_str()); FWD(set_heat_mode(ch, m)); }
void LoggingActions::set_band(int ch, double c) { LOGA("set_band ch%d %.1f", ch, c); FWD(set_band(ch, c)); }
void LoggingActions::set_anti_overshoot(int ch, bool on) { LOGA("set_anti_overshoot ch%d %d", ch, on); FWD(set_anti_overshoot(ch, on)); }
void LoggingActions::set_profile(int ch, const std::vector<CurveSeg>& s) {
    LOGA("set_profile ch%d %zu segs", ch, s.size());
    FWD(set_profile(ch, s));
}
void LoggingActions::start_profile(int ch) { LOGA("start_profile ch%d", ch); FWD(start_profile(ch)); }
void LoggingActions::set_valve(int ch, bool o) { LOGA("set_valve ch%d %d", ch, o); FWD(set_valve(ch, o)); }
void LoggingActions::set_gas_source(int k, bool o) { LOGA("set_gas_source %d %d", k, o); FWD(set_gas_source(k, o)); }
void LoggingActions::gas_zero(int ch) { LOGA("gas_zero ch%d", ch); FWD(gas_zero(ch)); }
void LoggingActions::set_solution_volume(int ch, double ml) { LOGA("set_solution_volume ch%d %.1f", ch, ml); FWD(set_solution_volume(ch, ml)); }
void LoggingActions::channel_enable(int ch, bool on) { LOGA("channel_enable ch%d %d", ch, on); FWD(channel_enable(ch, on)); }
void LoggingActions::copy_to_all(int ch) { LOGA("copy_to_all ch%d", ch); FWD(copy_to_all(ch)); }
void LoggingActions::abort_program(int ch) { LOGA("abort_program ch%d", ch); FWD(abort_program(ch)); }
void LoggingActions::ack_alarm(const std::string& id) { LOGA("ack_alarm %s", id.c_str()); FWD(ack_alarm(id)); }
void LoggingActions::ack_all() { LOGA("ack_all"); FWD(ack_all()); }
void LoggingActions::set_language(const std::string& c) { LOGA("set_language %s", c.c_str()); FWD(set_language(c)); }

// ---- 第二批 ----
void LoggingActions::put_program(int ch, const ProgramDoc& p) {
    LOGA("put_program ch%d %s", ch, program_to_json(p).c_str());
    FWD(put_program(ch, p));
}
void LoggingActions::start_program(int ch) { LOGA("start_program ch%d", ch); FWD(start_program(ch)); }
void LoggingActions::lib_save(bool tpl, const ProgramDoc& p) {
    LOGA("lib_save %s id=%s rev=%lld name=%s", tpl ? "templates" : "programs", p.id.c_str(), (long long)p.revision, p.name.c_str());
    FWD(lib_save(tpl, p));
}
void LoggingActions::lib_delete(bool tpl, const std::string& id, int64_t rev) {
    LOGA("lib_delete %s %s rev=%lld", tpl ? "templates" : "programs", id.c_str(), (long long)rev);
    FWD(lib_delete(tpl, id, rev));
}
void LoggingActions::lib_import_usb() { LOGA("lib_import_usb"); FWD(lib_import_usb()); }
void LoggingActions::leak_start(const std::vector<int>& chs) {
    std::string s;
    for (int c : chs) s += std::to_string(c) + " ";
    LOGA("leak_start %s", s.c_str());
    FWD(leak_start(chs));
}
void LoggingActions::leak_abort() { LOGA("leak_abort"); FWD(leak_abort()); }
void LoggingActions::run_note(const std::string& id, const std::string& n) { LOGA("run_note %s %s", id.c_str(), n.c_str()); FWD(run_note(id, n)); }
void LoggingActions::export_usb(const ExportReq& r) {
    std::string k;
    for (auto& x : r.kinds) k += x + " ";
    LOGA("export_usb run=%s file=%s day=%s from=%lld to=%lld kinds=%s step=%d", r.runId.c_str(), r.file.c_str(), r.day.c_str(),
         (long long)r.from, (long long)r.to, k.c_str(), r.stepSec);
    FWD(export_usb(r));
}
void LoggingActions::patch_setting(const std::string& key, double v) { LOGA("patch_setting %s=%g", key.c_str(), v); FWD(patch_setting(key, v)); }
void LoggingActions::patch_setting_str(const std::string& key, const std::string& v) {
    LOGA("patch_setting %s=%s", key.c_str(), v.c_str());
    FWD(patch_setting_str(key, v));
}
void LoggingActions::set_pid(int ch, int band, double p, double i, double d) {
    LOGA("set_pid ch%d band%d P=%g I=%g D=%g", ch, band, p, i, d);
    FWD(set_pid(ch, band, p, i, d));
}
void LoggingActions::set_pid_bands(double lo, double hi) { LOGA("set_pid_bands %g %g", lo, hi); FWD(set_pid_bands(lo, hi)); }
void LoggingActions::autotune(int ch, int band, double sp) { LOGA("autotune ch%d band%d sp=%g", ch, band, sp); FWD(autotune(ch, band, sp)); }
void LoggingActions::autotune_cancel(int ch) { LOGA("autotune_cancel ch%d", ch); FWD(autotune_cancel(ch)); }
void LoggingActions::calib_start(int ch, double w, bool redo) {
    LOGA("calib_start ch%d water=%g redo=%d", ch, w, redo);
    FWD(calib_start(ch, w, redo));
}
void LoggingActions::calib_continue(double w) { LOGA("calib_continue water=%g", w); FWD(calib_continue(w)); }
void LoggingActions::calib_abort() { LOGA("calib_abort"); FWD(calib_abort()); }
void LoggingActions::wifi_enable(bool on) { LOGA("wifi_enable %d", on); FWD(wifi_enable(on)); }
void LoggingActions::wifi_scan() { LOGA("wifi_scan"); FWD(wifi_scan()); }
void LoggingActions::wifi_connect(const std::string& ssid, const std::string& pw) {
    LOGA("wifi_connect %s (password %zu chars)", ssid.c_str(), pw.size());
    FWD(wifi_connect(ssid, pw));
}
void LoggingActions::wifi_forget(const std::string& ssid) { LOGA("wifi_forget %s", ssid.c_str()); FWD(wifi_forget(ssid)); }
void LoggingActions::wifi_disconnect() { LOGA("wifi_disconnect"); FWD(wifi_disconnect()); }
void LoggingActions::set_ip_mode(bool dhcp) { LOGA("set_ip_mode %s", dhcp ? "dhcp" : "manual"); FWD(set_ip_mode(dhcp)); }
void LoggingActions::comm_test(const std::string& m) { LOGA("comm_test %s", m.c_str()); FWD(comm_test(m)); }
void LoggingActions::comm_reconnect(const std::string& m) { LOGA("comm_reconnect %s", m.c_str()); FWD(comm_reconnect(m)); }
void LoggingActions::quit_app() { LOGA("quit_app"); FWD(quit_app()); }
void LoggingActions::control_grant(const std::string& id) { LOGA("control_grant %s", id.c_str()); FWD(control_grant(id)); }
void LoggingActions::control_deny(const std::string& id) { LOGA("control_deny %s", id.c_str()); FWD(control_deny(id)); }
void LoggingActions::control_take_back() { LOGA("control_take_back"); FWD(control_take_back()); }

}  // namespace reactor::hmi::model
