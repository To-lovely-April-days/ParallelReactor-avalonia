// 新增端点（doc/接口契约-新增.md §3.1–§3.7、§3.12）：设置 PATCH、档位分界、单路到位范围 / 防超调、手动阀控开关、全部确认、
// 界面语言、程序库、容积标定。「本地」= 只在 Unix 入口上开放（TCP 上 404）。
#include <algorithm>
#include <map>

#include "api/api_routes.hpp"
#include "api/api_system.hpp"
#include "store/store_util.hpp"

namespace reactor::api {

namespace {

using net::Request;
using net::RouteOptions;
using cmd::ActionClass;
using cmd::Commands;
using cmd::Done;
using cmd::Source;
using ctl::OpResult;

// PATCH /settings 的键（GET /settings 里的 JSON 路径）→ store 的设置项（接口契约 §3.1 表）
const std::map<std::string, store::SettingKey>& numeric_keys() {
    using K = store::SettingKey;
    static const std::map<std::string, K> m = {
        {"limits.tempSpMaxC", K::TMaxSp},           {"limits.pressureSpMaxPsi", K::PMaxSp},
        {"limits.rpmMax", K::RpmMax},               {"limits.volumeMaxMl", K::VolMax},
        {"alarms.overTempC", K::OverTemp},          {"alarms.overPressurePsi", K::OverPressure},
        {"alarms.heatTimeoutMin", K::HeatTimeout},  {"alarms.pressDeviationPsi", K::PressDeviation},
        {"leakTest.testPsi", K::LeakTestPsi},       {"leakTest.settleMin", K::LeakSettleMin},
        {"leakTest.measureMin", K::LeakMeasureMin}, {"leakTest.rateLimitPsiPerHr", K::LeakRate},
        {"leakTest.reminderDays", K::LeakReminderDays},
        {"approach.deltaC", K::ApproachDelta},      {"approach.fastRateCPerMin", K::ApproachFastRate},
        {"approach.rateCPerMin", K::ApproachRate},  {"approach.settleBandC", K::ApproachSettleBand},
        {"approach.settleSec", K::ApproachSettleSec}, {"approach.finalZoneC", K::ApproachFinalZone},
        {"stir.peakCurrentA", K::StirCurrent},      {"stir.standbyPct", K::StirStandbyPct},
        {"stir.rampMsPer1000Rpm", K::StirRampMs},   {"stir.microstep", K::StirMicrostep},
        {"stir.stirGearRatio", K::StirGearRatio},   {"pressure.fullScaleMPa", K::PressFullScaleMPa},
        {"volume.vesselVolume", K::VesselVolume},   {"volume.gasLineVolume", K::GasLineVolume},
        {"program.gasWaitMaxMin", K::ProgramGasWaitMin},
    };
    return m;
}

std::string dotted_of(const std::string& spec_name) {
    for (const auto& kv : numeric_keys())
        if (spec_name == store::setting_specs::of(kv.second).name) return kv.first;
    return spec_name;
}

struct UiChange {
    std::optional<std::string> pressure_unit, temp_unit, theme, keyboard;
    std::optional<bool> preflight;
    std::optional<double> retention;
};

// 程序库：store 的码 / 文案 → 接口契约 §3.7 的码
OpResult lib_error(const store::LibResult& r) {
    std::string code = r.code;
    if (code == "BAD_REQUEST" && r.message.find("同名") != std::string::npos) code = "NAME_CONFLICT";
    if (code == "BAD_REQUEST" && (r.message == "内置模板不能修改" || r.message == "内置模板不能删除" || r.message == "内置模板只能随软件提供"))
        code = "READ_ONLY";
    ojson detail = nullptr;
    if (code == "REVISION_CONFLICT" || code == "PRECONDITION_REQUIRED") detail = ojson{{"currentRevision", r.current_revision}};
    return OpResult::fail(code, r.message, "err", detail);
}

constexpr int kLibraryMax = 50;

}  // namespace

void Routes::local(ApiService& s, net::Router& r) {
    ApiService* self = &s;
    const std::string P = "/api/v1";
    auto W = [self](WriteSpec spec, Binder b) { return [self, spec, b](const Request& q) { return self->command(q, spec, b); }; };
    RouteOptions local{net::RateClass::Auto, false, true};

    // —— §3.5 POST /alarms/ack-all（两边，R） ——
    r.post(P + "/alarms/ack-all", W({"alarm.ackAll", ActionClass::Record}, [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [](Commands& c, const Source& src, Done d) { d(c.ack_all_alarms(src)); };
        return b;
    }));

    // —— §3.1 PATCH /settings（本地，R） ——
    r.patch(P + "/settings", W({"settings.patch", ActionClass::Record}, [self](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto rev = p.opt_int64("revision");
        if (p.bad()) return Bound::bad(p.error);
        if (!rev) return Bound::bad("缺少 revision（读到的 settings.revision）");
        auto vals = p.prop("values");
        if (!vals || !vals->is_object() || vals->empty()) return Bound::bad("缺少 values（要改的设置项）");
        std::vector<store::SettingChange> changes;
        std::vector<std::string> numeric_dotted;
        std::optional<bool> stop_heat;
        UiChange ui;
        auto bad_key = [](const std::string& k, const std::string& m) {
            Bound b = Bound::bad(m);
            b.detail = ojson{{"key", k}};
            return b;
        };
        auto enum_of = [](const std::string& key, const nlohmann::json& v, std::initializer_list<const char*> allowed,
                          std::optional<std::string>& out) -> std::optional<Bound> {
            if (!v.is_string()) return Bound::bad(key + " 须为字符串");
            std::string x = v.get<std::string>();
            for (const char* a : allowed)
                if (x == a) { out = x; return std::nullopt; }
            std::string all;
            for (const char* a : allowed) all += (all.empty() ? "" : " / ") + std::string(a);
            return Bound::reject("PARAM_OUT_OF_RANGE", key + " 只能是 " + all);
        };
        for (auto it = vals->begin(); it != vals->end(); ++it) {
            const std::string& k = it.key();
            const auto& v = it.value();
            auto nk = numeric_keys().find(k);
            if (nk != numeric_keys().end()) {
                if (!v.is_number()) return bad_key(k, k + " 须为数值");
                changes.push_back(store::SettingChange{nk->second, v.get<double>()});
                numeric_dotted.push_back(k);
            } else if (k == "alarms.stopHeatOnOverPressure") {
                if (!v.is_boolean()) return bad_key(k, k + " 须为 true / false");
                stop_heat = v.get<bool>();
            } else if (k == "ui.preflightChecklist") {
                if (!v.is_boolean()) return bad_key(k, k + " 须为 true / false");
                ui.preflight = v.get<bool>();
            } else if (k == "storage.retentionDays") {
                if (!v.is_number()) return bad_key(k, k + " 须为数值");
                double d = v.get<double>();
                if (!(d >= 7 && d <= 3650)) return Bound::reject("PARAM_OUT_OF_RANGE", "数据保留天数须在 7–3650 天 之间");
                ui.retention = d;
            } else if (k == "ui.pressureUnit") {
                if (auto e = enum_of(k, v, {"MPa", "bar", "psi"}, ui.pressure_unit)) return *e;
            } else if (k == "ui.tempUnit") {
                if (auto e = enum_of(k, v, {"C", "F"}, ui.temp_unit)) return *e;
            } else if (k == "ui.theme") {
                if (auto e = enum_of(k, v, {"light", "dark"}, ui.theme)) return *e;
            } else if (k == "ui.keyboard") {
                if (auto e = enum_of(k, v, {"num", "full"}, ui.keyboard)) return *e;
            } else {
                return bad_key(k, "不认识的设置项：" + k);
            }
        }
        int64_t expected = *rev;
        Bound b;
        b.exec = [self, changes, stop_heat, ui, expected](Commands& c, const Source& src, Done d) {
            // 记下界面偏好里哪几项真的变了（changed 列表用）
            auto cur = self->d_.core->settings().settings;
            std::vector<std::string> ui_changed;
            if (ui.pressure_unit && *ui.pressure_unit != cur.ui_pressure_unit) ui_changed.push_back("ui.pressureUnit");
            if (ui.temp_unit && *ui.temp_unit != cur.ui_temp_unit) ui_changed.push_back("ui.tempUnit");
            if (ui.theme && *ui.theme != cur.ui_theme) ui_changed.push_back("ui.theme");
            if (ui.keyboard && *ui.keyboard != cur.ui_keyboard) ui_changed.push_back("ui.keyboard");
            if (ui.preflight && *ui.preflight != cur.ui_preflight_checklist) ui_changed.push_back("ui.preflightChecklist");
            if (ui.retention && *ui.retention != cur.storage_retention_days) ui_changed.push_back("storage.retentionDays");
            auto extra = [ui](store::AppSettings& a) {
                bool ch = false;
                auto set_s = [&](const std::optional<std::string>& v, std::string& dst) {
                    if (v && *v != dst) { dst = *v; ch = true; }
                };
                set_s(ui.pressure_unit, a.ui_pressure_unit);
                set_s(ui.temp_unit, a.ui_temp_unit);
                set_s(ui.theme, a.ui_theme);
                set_s(ui.keyboard, a.ui_keyboard);
                if (ui.preflight && *ui.preflight != a.ui_preflight_checklist) { a.ui_preflight_checklist = *ui.preflight; ch = true; }
                if (ui.retention && *ui.retention != a.storage_retention_days) { a.storage_retention_days = *ui.retention; ch = true; }
                return ch;
            };
            c.patch_settings(src, changes, stop_heat, extra, expected, [d, ui_changed](const OpResult& r) {
                OpResult out = r;
                if (!r.ok) {
                    if (out.data.is_object() && out.data.contains("key") && out.data["key"].is_string())
                        out.data["key"] = dotted_of(out.data["key"].get<std::string>());
                    return d(out);
                }
                ojson changed = ojson::array();
                if (r.data.contains("changed"))
                    for (const auto& n : r.data["changed"]) changed.push_back(dotted_of(n.get<std::string>()));
                if (r.data.value("stopHeatChanged", false)) changed.push_back("alarms.stopHeatOnOverPressure");
                for (const auto& k : ui_changed) changed.push_back(k);
                out.data = ojson{{"revision", r.data["revision"]}, {"changed", changed}};
                d(out);
            });
        };
        return b;
    }), local);

    // —— §3.2 PUT /pid/bands（本地，C） ——
    r.put(P + "/pid/bands", W({"pid.bands", ActionClass::Control}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto lo = p.num("lowMidC");
        auto hi = p.num("midHighC");
        auto rev = p.opt_int64("revision");
        if (!p.bad() && (!lo || !hi)) p.fail("缺少 lowMidC / midHighC");
        if (p.bad()) return Bound::bad(p.error);
        double a = *lo, b2 = *hi;
        Bound b;
        b.exec = [a, b2, rev](Commands& c, const Source& src, Done d) { d(c.set_band_bounds(src, a, b2, rev)); };
        return b;
    }), local);

    // —— §3.3 单路到位范围 / 防超调（两边，C） ——
    r.put(P + "/channels/{id}/temperature/band", W({"temp.band", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto id = parse_channel(q.param("id"));
        if (!id) return Bound::reject("CHANNEL_NOT_FOUND", "通道 " + q.param("id") + " 不存在（有效范围 1–8）");
        Params p(body);
        auto v = p.num("bandC");
        if (!p.bad() && !v) p.fail("缺少 bandC");
        if (p.bad()) return Bound::bad(p.error);
        int ch = *id;
        double band = *v;
        Bound b;
        b.exec = [ch, band](Commands& c, const Source& src, Done d) { d(c.set_band(src, ch, band)); };
        return b;
    }));
    r.put(P + "/channels/{id}/temperature/anti-overshoot", W({"temp.antiOvershoot", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto id = parse_channel(q.param("id"));
        if (!id) return Bound::reject("CHANNEL_NOT_FOUND", "通道 " + q.param("id") + " 不存在（有效范围 1–8）");
        Params p(body);
        auto v = p.boolean("on");
        if (!p.bad() && !v) p.fail("缺少 on（true = 开，false = 关）");
        if (p.bad()) return Bound::bad(p.error);
        int ch = *id;
        bool on = *v;
        Bound b;
        b.exec = [ch, on](Commands& c, const Source& src, Done d) { d(c.set_anti_overshoot(src, ch, on)); };
        return b;
    }));

    // —— §3.4 PUT /gas/manual（本地；开 = C、关 = S） ——
    r.put(P + "/gas/manual", [self](const Request& q) {
        auto pb = parse_body(q.body);
        bool on = true;
        if (pb.error.empty()) {
            Params p(pb.ptr());
            auto v = p.boolean("on");
            if (!p.bad() && v) on = *v;
        }
        WriteSpec spec{"valve.manualMode", on ? ActionClass::Control : ActionClass::Stop};
        return self->command(q, spec, [](const nlohmann::json* body, const Request&, const Source&) {
            Params p(body);
            auto v = p.boolean("on");
            if (!p.bad() && !v) p.fail("缺少 on（true = 开，false = 关）");
            if (p.bad()) return Bound::bad(p.error);
            bool o = *v;
            Bound b;
            b.exec = [o](Commands& c, const Source& src, Done d) { d(c.set_manual_valve_mode(src, o)); };
            return b;
        });
    }, local);

    // —— §3.6 PUT /system/language（本地，R） ——
    r.put(P + "/system/language", W({"system.language", ActionClass::Record}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto code = p.str("code");
        if (!p.bad() && !code) p.fail("缺少 code（zh / en / ja / ko / de / fr / es / ru）");
        if (p.bad()) return Bound::bad(p.error);
        std::string c2 = *code;
        Bound b;
        b.exec = [c2](Commands& c, const Source& src, Done d) { d(c.set_language(src, c2)); };
        return b;
    }), local);

    // POST /system/restart（本地，S；设计文档 §5.2「退出 / 重启服务」）：主循环退出，systemd Restart=always 再拉起
    r.post(P + "/system/restart", W({"system.restart", ActionClass::Stop}, [self](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [self](Commands&, const Source&, Done d) {
            if (!self->d_.request_exit) return d(OpResult::fail("HW_NOT_CONFIGURED", "这个进程不支持从接口重启"));
            d(OpResult::accept("正在重启后台服务 · 退出时会停加热、关阀、停搅拌", "warn", ojson::object()));
            auto fn = self->d_.request_exit;
            std::thread([fn] {
                std::this_thread::sleep_for(std::chrono::milliseconds(300));
                fn();
            }).detach();
        };
        return b;
    }), local);

    // —— §3.7 程序库（两边） ——
    auto list_lib = [self](store::LibKind k) {
        return [self, k](const Request&) {
            if (!self->d_.library) return api_error("INTERNAL", "没有配置程序库");
            std::array<std::optional<std::string>, 8> src;
            {
                std::lock_guard<std::mutex> lk(self->prog_mu_);
                for (size_t i = 0; i < 8; ++i) src[i] = self->prog_[i].source_id;
            }
            auto entries = self->d_.library->list(k);
            if (k == store::LibKind::Template)
                std::stable_sort(entries.begin(), entries.end(), [](const store::LibEntry& a, const store::LibEntry& b) {
                    return a.body.value("builtIn", false) && !b.body.value("builtIn", false);
                });
            ojson items = ojson::array();
            for (const auto& e : entries) {
                ojson j = e.body;
                j["revision"] = e.revision;
                if (k == store::LibKind::Template && !j.contains("builtIn")) j["builtIn"] = false;
                ojson used = ojson::array();
                for (size_t i = 0; i < 8; ++i)
                    if (src[i] && *src[i] == e.id) used.push_back(static_cast<int>(i + 1));
                j["usedBy"] = used;
                items.push_back(j);
            }
            ojson out{{"count", items.size()}};
            if (k == store::LibKind::Program) out["max"] = kLibraryMax;
            out["items"] = items;
            return ok_json(out);
        };
    };
    r.get(P + "/library/programs", list_lib(store::LibKind::Program));
    r.get(P + "/library/templates", list_lib(store::LibKind::Template));
    auto lib_item = [self](store::LibKind k) {
        return [self, k](const Request& q) {
            if (!self->d_.library) return api_error("INTERNAL", "没有配置程序库");
            auto e = self->d_.library->get(k, q.param("id"));
            if (!e) return api_error("NOT_FOUND", k == store::LibKind::Program ? "没有这个程序" : "没有这个模板");
            ojson j = e->body;
            j["revision"] = e->revision;
            return ok_json(j);
        };
    };
    r.get(P + "/library/programs/{id}", lib_item(store::LibKind::Program));
    r.get(P + "/library/templates/{id}", lib_item(store::LibKind::Template));

    // 写：modified / by 由服务端按调用者身份填（决定记录），客户端给的忽略
    auto stamp = [self](store::LibKind k, ojson& body, const Source& src) {
        std::string now = store::fmt_wall(store::WallTime::local(self->now_wall()), "yyyy-MM-ddTHH:mm:ss");
        std::string by = src.is_remote() ? src.client_id : "触屏";
        ojson& p = k == store::LibKind::Template ? body["program"] : body;
        if (!p.is_object()) return;
        p["modified"] = now;
        p["by"] = by;
    };
    auto lib_done = [self](const char* what, store::LibKind k, const store::LibResult& res, Done d) {
        if (!res.ok) return d(lib_error(res));
        self->bump_library();
        std::string id = res.entry ? res.entry->id : "";
        if (self->d_.logger)
            self->d_.logger->event(store::WallTime::local(self->now_wall()), "info", "程序库",
                                   std::string(k == store::LibKind::Program ? "程序" : "模板") + what,
                                   id + (res.entry && res.entry->body.contains("name") ? " · " + res.entry->body.value("name", "") : ""),
                                   "system", std::nullopt, std::nullopt, "library.changed");
        ojson modified = nullptr;
        if (res.entry) {
            const ojson& p = k == store::LibKind::Template ? res.entry->body.value("program", ojson::object()) : res.entry->body;
            modified = p.value("modified", ojson());
        }
        d(OpResult::done(std::string(k == store::LibKind::Program ? "程序" : "模板") + what, "ok",
                         ojson{{"id", id}, {"revision", res.entry ? ojson(res.entry->revision) : ojson(nullptr)}, {"modified", modified}}));
    };
    for (auto k : {store::LibKind::Program, store::LibKind::Template}) {
        std::string base = P + (k == store::LibKind::Program ? "/library/programs" : "/library/templates");
        WriteSpec create{k == store::LibKind::Program ? "library.programCreate" : "library.templateCreate", ActionClass::Record, true};
        create.inline_exec = true;
        r.post(base, W(create, [self, k, stamp, lib_done](const nlohmann::json* body, const Request&, const Source&) {
            if (!body) return Bound::bad(std::string(k == store::LibKind::Program ? "程序" : "模板") + "条目必须是 JSON 对象");
            ojson b0 = ojson::parse(body->dump());
            Bound b;
            b.exec = [self, k, b0, stamp, lib_done](Commands&, const Source& src, Done d) {
                if (!self->d_.library) return d(OpResult::fail("INTERNAL", "没有配置程序库"));
                if (k == store::LibKind::Program && static_cast<int>(self->d_.library->list(k).size()) >= kLibraryMax)
                    return d(OpResult::fail("LIBRARY_FULL", "程序库已满（最多 " + std::to_string(kLibraryMax) + " 个）· 请先删掉不用的程序"));
                ojson body2 = b0;
                body2.erase("revision");
                body2.erase("usedBy");
                if (k == store::LibKind::Template) body2.erase("builtIn");
                stamp(k, body2, src);
                lib_done("已保存", k, self->d_.library->create(k, body2), d);
            };
            return b;
        }));
        WriteSpec upd{k == store::LibKind::Program ? "library.programUpdate" : "library.templateUpdate", ActionClass::Record};
        upd.inline_exec = true;
        r.put(base + "/{id}", W(upd, [self, k, stamp, lib_done](const nlohmann::json* body, const Request& q, const Source&) {
            if (!body) return Bound::bad(std::string(k == store::LibKind::Program ? "程序" : "模板") + "条目必须是 JSON 对象");
            Params p(body);
            auto rev = p.opt_int64("revision");
            if (p.bad()) return Bound::bad(p.error);
            ojson b0 = ojson::parse(body->dump());
            std::string id = q.param("id");
            Bound b;
            b.exec = [self, k, b0, id, rev, stamp, lib_done](Commands&, const Source& src, Done d) {
                if (!self->d_.library) return d(OpResult::fail("INTERNAL", "没有配置程序库"));
                ojson body2 = b0;
                body2.erase("revision");
                body2.erase("usedBy");
                stamp(k, body2, src);
                lib_done("已保存", k, self->d_.library->update(k, id, body2, rev), d);
            };
            return b;
        }));
        WriteSpec del{k == store::LibKind::Program ? "library.programDelete" : "library.templateDelete", ActionClass::Record};
        del.inline_exec = true;
        r.del(base + "/{id}", W(del, [self, k](const nlohmann::json*, const Request& q, const Source&) {
            std::optional<int64_t> rev;
            if (q.has_query("revision") && !q.query_param("revision").empty()) {
                char* e = nullptr;
                long long v = std::strtoll(q.query_param("revision").c_str(), &e, 10);
                if (!e || *e) return Bound::bad("revision 须为整数");
                rev = v;
            }
            std::string id = q.param("id");
            Bound b;
            b.exec = [self, k, id, rev](Commands&, const Source&, Done d) {
                if (!self->d_.library) return d(OpResult::fail("INTERNAL", "没有配置程序库"));
                auto res = self->d_.library->remove(k, id, rev);
                if (!res.ok) return d(lib_error(res));
                self->bump_library();
                if (self->d_.logger)
                    self->d_.logger->event(store::WallTime::local(self->now_wall()), "info", "程序库",
                                           std::string(k == store::LibKind::Program ? "程序" : "模板") + "已删除", id, "system", std::nullopt,
                                           std::nullopt, "library.deleted");
                d(OpResult::done(std::string(k == store::LibKind::Program ? "程序" : "模板") + "已删除", "ok", ojson{{"id", id}}));
            };
            return b;
        }));
    }

    // POST /library/import（本地，R，幂等必填）：从 U 盘根目录读 PC 的 library.json / templates.json
    WriteSpec imp{"library.import", ActionClass::Record, true};
    imp.inline_exec = true;
    r.post(P + "/library/import", W(imp, [self](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto source = p.str("source");
        if (p.bad()) return Bound::bad(p.error);
        if (source.value_or("usb") != "usb") return Bound::bad("source 目前只支持 usb");
        Bound b;
        b.exec = [self](Commands&, const Source&, Done d) {
            if (!self->d_.library) return d(OpResult::fail("INTERNAL", "没有配置程序库"));
            auto vols = list_usb(self->d_.usb_root);
            if (vols.empty()) return d(OpResult::fail("USB_NOT_PRESENT", "没有检测到 U 盘 · 请插好后再导入"));
            for (const auto& v : vols) {
                std::string lib = store::join_path(v.path, "library.json"), tpl = store::join_path(v.path, "templates.json");
                bool has_lib = store::file_exists(lib), has_tpl = store::file_exists(tpl);
                if (!has_lib && !has_tpl) continue;
                store::ProgramLibrary::ImportResult a, t;
                if (has_lib) a = self->d_.library->import_pc_library(lib);
                if (has_tpl) t = self->d_.library->import_pc_templates(tpl);
                self->bump_library();
                if (self->d_.logger)
                    self->d_.logger->event(store::WallTime::local(self->now_wall()), "info", "程序库", "从 U 盘导入程序库",
                                           v.label + " · 程序 " + std::to_string(a.added) + " 个 · 模板 " + std::to_string(t.added) + " 个",
                                           "system", std::nullopt, std::nullopt, "library.imported");
                return d(OpResult::done("已从 U 盘导入 " + std::to_string(a.added + t.added) + " 个程序 / 模板", "ok",
                                        ojson{{"programs", ojson{{"added", a.added}, {"skipped", a.skipped}}},
                                              {"templates", ojson{{"added", t.added}, {"skipped", t.skipped}}}}));
            }
            d(OpResult::fail("NOT_FOUND", "U 盘根目录里没有 library.json / templates.json"));
        };
        return b;
    }), local);

    // —— §3.12 容积标定（本地） ——
    r.post(P + "/calib", W({"calib.start", ActionClass::Control, true}, [self](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto chv = p.opt_int("channel");
        auto water = p.num("waterMl");
        if (!p.bad() && !chv) p.fail("缺少 channel");
        if (p.bad()) return Bound::bad(p.error);
        int ch = *chv;
        Bound b;
        b.exec = [self, ch, water](Commands& c, const Source& src, Done d) {
            auto& vc = self->d_.automation->volume_calib();
            if (vc.running()) return d(OpResult::fail("CALIB_RUNNING", "已经在做容积标定 · 请等它做完或先中止"));
            if (water) vc.set_water_ml(*water);
            auto r = c.start_calib(src, ch, false, water.value_or(0), water.has_value());
            if (r.ok) r.data = ojson{{"channel", ch}};
            d(r);
        };
        return b;
    }), local);
    r.post(P + "/calib/continue", W({"calib.continue", ActionClass::Control}, [self](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [self](Commands& c, const Source& src, Done d) {
            auto& vc = self->d_.automation->volume_calib();
            if (!vc.awaiting_water() || !vc.first_pass_channel())
                return d(OpResult::fail("CALIB_NOT_WAITING", "现在没有在等加水 · 不需要继续"));
            auto snapc = vc.snapshot();
            double water = snapc.is_object() && snapc.contains("waterMl") && snapc["waterMl"].is_number() ? snapc["waterMl"].get<double>() : 5;
            auto r = c.start_calib(src, *vc.first_pass_channel(), true, water, false);
            if (r.ok) r.data = ojson::object();
            d(r);
        };
        return b;
    }), local);
    r.del(P + "/calib/current", W({"calib.abort", ActionClass::Stop}, [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [](Commands& c, const Source& src, Done d) {
            auto r = c.abort_calib(src);
            if (r.ok) r.data = ojson::object();
            d(r);
        };
        return b;
    }), local);
    r.get(P + "/calib", [self](const Request&) {
        auto b = self->snap();
        if (!b) return starting_response();
        const ojson& c = b->calib;
        if (!c.is_object()) return ok_json(ojson{{"running", false}, {"channel", nullptr}, {"readings", ojson::array()}, {"pass", 0}, {"passes", 0},
                                                 {"cycle", 0}, {"cycles", 0}, {"stepMinutes", ojson::array({0, 0, 0, 0})},
                                                 {"vesselTempC", nullptr}, {"gas", "惰性气体"}});
        ojson readings = ojson::array();
        if (c.contains("points") && c["points"].is_array()) {
            int n = 0;
            for (const auto& pt : c["points"]) {
                ++n;
                ojson a = pt.is_array() && pt.size() > 0 ? pt[0] : ojson(nullptr), z = pt.is_array() && pt.size() > 1 ? pt[1] : ojson(nullptr);
                readings.push_back(ojson{{"n", n}, {"manifoldFromPsi", a}, {"manifoldToPsi", z}, {"vesselFromPsi", nullptr}, {"vesselToPsi", nullptr}});
            }
        }
        std::string stage = c.value("stage", ojson("")).is_string() ? c.value("stage", std::string()) : std::string();
        bool two = c.value("twoRuns", false);
        int pass = stage == "second" || stage == "done" ? (two ? 2 : 1) : 1;
        ojson ch = c.contains("channel") ? c["channel"] : ojson(nullptr);
        ojson temp = nullptr;
        if (ch.is_number_integer() && ch.get<int>() >= 1 && ch.get<int>() <= 8) temp = opt_num(b->channels[static_cast<size_t>(ch.get<int>() - 1)].temp_c);
        return ok_json(ojson{{"running", c.value("running", false)}, {"channel", ch}, {"readings", readings}, {"pass", pass},
                             {"passes", two ? 2 : 1}, {"cycle", readings.size()}, {"cycles", readings.size()},
                             {"stepMinutes", ojson::array({0, 0, 0, 0})}, {"vesselTempC", temp}, {"gas", "惰性气体"},
                             {"stage", stage}, {"waitingOperator", c.value("waitingOperator", false)}});
    }, local);
}

}  // namespace reactor::api
