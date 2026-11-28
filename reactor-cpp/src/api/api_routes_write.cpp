// 写端点（C#: Api/Endpoints.Write.cs MapWrites、Api/Endpoints.Sessions.cs 的 POST / DELETE、Api/Hmi/HmiRemoteCommands.cs）。
// 每个端点 = WriteSpec + 参数绑定 + 一个 cmd::Commands 调用（在控制线程上执行）。
#include "api/api_routes.hpp"
#include <cmath>

#include "core/op_codes.hpp"
#include <cctype>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::api {

namespace {

using net::Request;
using net::Response;
using net::RouteOptions;
using net::RateClass;
using cmd::ActionClass;
using cmd::Commands;
using cmd::Done;
using cmd::Source;
using ctl::OpResult;

Exec sync(std::function<OpResult(Commands&, const Source&)> fn) {
    return [fn](Commands& c, const Source& s, Done d) { d(fn(c, s)); };
}

struct ChannelArg {
    std::optional<int> id;
    Bound reject;
};
ChannelArg channel_arg(const Request& q, const char* name = "id") {
    ChannelArg a;
    std::string raw = q.param(name);
    a.id = parse_channel(raw);
    if (!a.id) a.reject = Bound::reject("CHANNEL_NOT_FOUND", "通道 " + raw + " 不存在（有效范围 1–8）");
    return a;
}

Bound bad_or(Params& p, Exec e) {
    if (p.bad()) return Bound::bad(p.error);
    Bound b;
    b.exec = std::move(e);
    return b;
}

// 先看正文决定动作类（同 C# ValveOpen）：只有 open=true 是控制类；读不出来按控制类（管线里再报 400）
bool valve_open_peek(const std::string& raw) {
    auto b = parse_body(raw);
    if (!b.error.empty()) return true;
    Params p(b.ptr());
    auto v = p.boolean("open");
    if (p.bad()) return true;
    return v.value_or(true) != false;
}

int gas_inlet_index(const std::string& gid_in) {
    std::string g;
    for (char c : trim(gid_in)) g += static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    if (g == "inert" || g == "1") return 0;
    if (g == "a" || g == "2") return 1;
    if (g == "b" || g == "3") return 2;
    return -1;
}

}  // namespace

void Routes::write(ApiService& s, net::Router& r) {
    ApiService* self = &s;
    const std::string P = "/api/v1";
    auto W = [self](WriteSpec spec, Binder b) { return [self, spec, b](const Request& q) { return self->command(q, spec, b); }; };

    // —— 全部停止 / 兼容急停 ——
    r.post(P + "/estop", W({"estop", ActionClass::Stop, false, net::Role::Operator, true},
                           [](const nlohmann::json* b, const Request&, const Source&) {
                               Params p(b);
                               auto note = p.note("note");
                               std::string n = note.value_or("");
                               return bad_or(p, sync([n](Commands& c, const Source& s) { return c.estop(s, n); }));
                           }),
           RouteOptions{RateClass::Estop, false, false});

    r.post(P + "/stop-all", W({"stopAll", ActionClass::Stop}, [](const nlohmann::json* b, const Request&, const Source&) {
        Params p(b);
        bool stir = p.boolean("stopStir").value_or(false);
        return bad_or(p, [stir](Commands& c, const Source& s, Done d) { c.stop_all(s, stir, d); });
    }));

    // —— 运行 / 通道 ——
    r.post(P + "/run", W({"run", ActionClass::Control, true}, [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = sync([](Commands& c, const Source& s) { return c.run(s, false); });
        return b;
    }));

    r.post(P + "/channels/{id}/enable", W({"channel.enable", ActionClass::Control}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.enable_channel(s, id, true); });
        return b;
    }));

    r.post(P + "/channels/{id}/start", W({"channel.start", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto t = p.num("tempC");
        return bad_or(p, sync([id, t](Commands& c, const Source& s) { return c.start_channel(s, id, t); }));
    }));

    for (bool disable : {false, true}) {
        r.post(P + (disable ? "/channels/{id}/disable" : "/channels/{id}/stop"),
               W({disable ? "channel.disable" : "channel.stop", ActionClass::Stop}, [disable](const nlohmann::json*, const Request& q, const Source&) {
                   auto ch = channel_arg(q);
                   if (!ch.id) return ch.reject;
                   int id = *ch.id;
                   Bound b;
                   b.exec = [id, disable](Commands& c, const Source& s, Done d) { c.stop_channel(s, id, disable, d); };
                   return b;
               }));
    }

    r.post(P + "/channels/copy-config", W({"channel.copyConfig", ActionClass::Control, true}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto from_raw = p.require("from");
        if (p.bad()) return Bound::bad(p.error);
        auto from = parse_channel(*from_raw);
        if (!from) return Bound::reject("CHANNEL_NOT_FOUND", "通道 " + *from_raw + " 不存在（有效范围 1–8）");
        auto to = p.int_list("to", false);
        int f = *from;
        return bad_or(p, sync([f, to](Commands& c, const Source& s) { return c.copy_channel_config(s, f, to); }));
    }));

    r.put(P + "/channels/{id}/volume", W({"channel.volume", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto ml = p.num("ml");
        if (!p.bad() && !ml) p.fail("缺少 ml（溶液体积 mL）");
        double v = ml.value_or(0);
        return bad_or(p, sync([id, v](Commands& c, const Source& s) { return c.set_volume(s, id, v); }));
    }));

    r.post(P + "/channels/{id}/gas-counter/reset", W({"channel.gasCounterReset", ActionClass::Control}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.reset_gas_counter(s, id); });
        return b;
    }));

    // —— 温度 ——
    r.post(P + "/channels/{id}/heat/start", W({"heat.start", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto t = p.num("tempC");
        return bad_or(p, sync([id, t](Commands& c, const Source& s) { return c.start_heat(s, id, t); }));
    }));

    r.post(P + "/channels/{id}/heat/stop", W({"heat.stop", ActionClass::Stop}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = [id](Commands& c, const Source& s, Done d) { c.stop_heat(s, id, d); };
        return b;
    }));

    r.put(P + "/channels/{id}/temperature/setpoint", W({"temp.setpoint", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto t = p.num("tempC");
        if (!p.bad() && !t) p.fail("缺少 tempC");
        double v = t.value_or(0);
        return bad_or(p, sync([id, v](Commands& c, const Source& s) { return c.set_setpoint(s, id, v); }));
    }));

    r.put(P + "/channels/{id}/temperature/mode", W({"temp.mode", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto m = p.str("spMode");
        if (!p.bad() && !m) p.fail("缺少 spMode（fixed / curve）");
        std::string mode = m.value_or("");
        return bad_or(p, sync([id, mode](Commands& c, const Source& s) { return c.set_sp_mode(s, id, mode); }));
    }));

    r.put(P + "/channels/{id}/profile", W({"profile.replace", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto arr = p.prop("segments");
        if (!arr || !arr->is_array()) return Bound::bad("缺少 segments 数组");
        std::vector<std::pair<double, double>> segs;
        for (const auto& seg : *arr) {
            Params sp(&seg);
            auto t = sp.num("tempC");
            if (sp.bad()) return Bound::bad(sp.error);
            if (!t) return Bound::bad("每段须有 tempC");
            auto m = sp.num("minutes");
            if (sp.bad()) return Bound::bad(sp.error);
            if (!m) return Bound::bad("每段须有 minutes");
            segs.emplace_back(*t, *m);
        }
        if (segs.empty()) return Bound::bad("segments 不能为空");
        Bound b;
        b.exec = sync([id, segs](Commands& c, const Source& s) { return c.replace_profile(s, id, segs); });
        return b;
    }));

    r.post(P + "/channels/{id}/profile/start", W({"profile.start", ActionClass::Control}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.start_profile(s, id); });
        return b;
    }));

    r.post(P + "/channels/{id}/profile/stop", W({"profile.stop", ActionClass::Stop}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.stop_profile(s, id); });
        return b;
    }));

    // —— PID（admin）/ 自整定 ——
    r.put(P + "/channels/{id}/pid/{band}", W({"pid.set", ActionClass::Control, false, net::Role::Admin},
                                             [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        std::string bs = q.param("band");
        int band = -1;
        if (bs.size() == 1 && bs[0] >= '0' && bs[0] <= '2') band = bs[0] - '0';
        if (band < 0) return Bound::bad("band 须为 0–2 的整数");
        Params p(body);
        auto pv = p.num("p");
        if (!p.bad() && !pv) p.fail("缺少 p");
        auto iv = p.num("i");
        if (!p.bad() && !iv) p.fail("缺少 i");
        auto dv = p.num("d");
        if (!p.bad() && !dv) p.fail("缺少 d");
        double P_ = pv.value_or(0), I_ = iv.value_or(0), D_ = dv.value_or(0);
        return bad_or(p, [id, band, P_, I_, D_](Commands& c, const Source& s, Done d) { c.set_pid(s, id, band, P_, I_, D_, d); });
    }));

    r.post(P + "/channels/{id}/autotune", W({"autotune.start", ActionClass::Control}, [](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto t = p.num("tempC");
        if (!p.bad() && !t) p.fail("缺少 tempC");
        auto band = p.opt_int("band");
        double tc = t.value_or(0);
        return bad_or(p, [id, tc, band](Commands& c, const Source& s, Done d) { c.start_autotune(s, id, tc, band, d); });
    }));

    r.del(P + "/channels/{id}/autotune", W({"autotune.cancel", ActionClass::Stop}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = [id](Commands& c, const Source& s, Done d) { c.cancel_autotune(s, id, d); };
        return b;
    }));

    // —— 搅拌：只有 {on:false}（不带 rpm）是停止类 ——
    r.put(P + "/stir", [self](const Request& q) {
        auto pb = parse_body(q.body);
        bool stop_class = false;
        if (pb.error.empty()) {
            Params p(pb.ptr());
            auto on = p.boolean("on");
            auto rpm = p.num("rpm");
            stop_class = !p.bad() && on && !*on && !rpm;
        }
        WriteSpec spec{"stir.set", stop_class ? ActionClass::Stop : ActionClass::Control};
        return self->command(q, spec, [](const nlohmann::json* body, const Request&, const Source&) {
            Params p(body);
            auto on = p.boolean("on");
            auto rpm = p.num("rpm");
            if (p.bad()) return Bound::bad(p.error);
            if (!on && !rpm) return Bound::bad("on / rpm 至少给一个");
            std::optional<int> rr;
            if (rpm) rr = static_cast<int>(std::nearbyint(*rpm));   // C# Math.Round：四舍六入五取偶
            Bound b;
            b.exec = [on, rr](Commands& c, const Source& s, Done d) { c.set_stir(s, on, rr, d); };
            return b;
        });
    });

    // —— 阀门（v0.7）：开 = 控制类，关 = 停止类 ——
    auto required_open = [](Params& p) {
        auto v = p.boolean("open");
        if (!p.bad() && !v) p.fail("缺少 open（true = 开，false = 关）");
        return v.value_or(false);
    };
    r.put(P + "/channels/{id}/valve", [self, required_open](const Request& q) {
        bool open = valve_open_peek(q.body);
        WriteSpec spec{"valve.channel", open ? ActionClass::Control : ActionClass::Stop};
        return self->command(q, spec, [required_open](const nlohmann::json* body, const Request& rq, const Source&) {
            auto ch = channel_arg(rq);
            if (!ch.id) return ch.reject;
            int id = *ch.id;
            Params p(body);
            bool o = required_open(p);
            return bad_or(p, [id, o](Commands& c, const Source& s, Done d) { c.set_reactor_valve(s, id, o, d); });
        });
    });
    r.put(P + "/gas/inlets/{gid}", [self, required_open](const Request& q) {
        bool open = valve_open_peek(q.body);
        WriteSpec spec{"valve.gasInlet", open ? ActionClass::Control : ActionClass::Stop};
        return self->command(q, spec, [required_open](const nlohmann::json* body, const Request& rq, const Source&) {
            int idx = gas_inlet_index(rq.param("gid"));
            if (idx < 0) return Bound::reject("NOT_FOUND", "气源 " + rq.param("gid") + " 不存在（inert / A / B）");
            Params p(body);
            bool o = required_open(p);
            return bad_or(p, [idx, o](Commands& c, const Source& s, Done d) { c.set_gas_inlet(s, idx, o, d); });
        });
    });
    r.put(P + "/gas/vent", [self, required_open](const Request& q) {
        bool open = valve_open_peek(q.body);
        WriteSpec spec{"valve.vent", open ? ActionClass::Control : ActionClass::Stop};
        return self->command(q, spec, [required_open](const nlohmann::json* body, const Request&, const Source&) {
            Params p(body);
            bool o = required_open(p);
            return bad_or(p, [o](Commands& c, const Source& s, Done d) { c.set_vent(s, o, d); });
        });
    });

    // —— 泄漏测试 ——
    r.post(P + "/leak-tests", W({"leakTest.start", ActionClass::Control, true}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto chs = p.int_list("channels", true);
        auto psi = p.num("testPsi");
        std::vector<int> c2 = chs.value_or(std::vector<int>{});
        return bad_or(p, sync([c2, psi](Commands& c, const Source& s) { return c.start_leak_test(s, c2, psi); }));
    }));
    r.del(P + "/leak-tests/current", W({"leakTest.abort", ActionClass::Stop}, [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = sync([](Commands& c, const Source& s) { return c.abort_leak_test(s); });
        return b;
    }));

    // —— 程序 ——
    r.put(P + "/channels/{id}/program", W({"program.replace", ActionClass::Control}, [self](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto steps = p.prop("steps");
        if (!steps || !steps->is_array()) return Bound::bad("缺少 steps 数组");
        auto name = p.str("name");          // 接口契约 §3.3（可选）：程序名
        auto source = p.str("sourceId");    // 接口契约 §3.3（可选）：来自程序库哪一条
        if (p.bad()) return Bound::bad(p.error);
        if (name && utf16_len(trim(*name)) > 40) return Bound::reject("PARAM_OUT_OF_RANGE", "程序名最多 40 个字");
        nlohmann::json st = *steps;
        Bound b;
        b.exec = [self, id, st, name, source](Commands& c, const Source& s, Done d) {
            auto r = c.replace_program(s, id, st, name ? std::optional<std::string>(trim(*name)) : std::nullopt);
            if (r.ok) {
                std::lock_guard<std::mutex> lk(self->prog_mu_);
                auto& pi = self->prog_[static_cast<size_t>(id - 1)];
                pi.name = name ? trim(*name) : std::string();
                pi.source_id = source;
                ++pi.revision;
                pi.modified_ms = self->now_wall();
                pi.edited_not_sent = true;
                pi.steps = r.data.is_object() && r.data.contains("steps") ? r.data["steps"] : ojson::array();
                self->bump_programs();
            }
            d(r);
        };
        return b;
    }));

    r.post(P + "/channels/{id}/program/run", W({"program.run", ActionClass::Control, true}, [self](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto sid = p.str("sessionId");
        if (p.bad()) return Bound::bad(p.error);
        if (sid) {
            auto cur = self->d_.sessions ? self->d_.sessions->current_id() : std::nullopt;
            if (!cur || *cur != *sid)
                return Bound::bad("sessionId「" + *sid + "」不是当前活动会话（" + cur.value_or("无") + "）");
        }
        Bound b;
        b.exec = [self, id](Commands& c, const Source& s, Done d) {
            auto r = c.run_program(s, id);
            if (r.ok) {
                std::lock_guard<std::mutex> lk(self->prog_mu_);
                self->prog_[static_cast<size_t>(id - 1)].edited_not_sent = false;
                self->bump_programs();
            }
            d(r);
        };
        return b;
    }));

    r.del(P + "/channels/{id}/program/run", W({"program.abort", ActionClass::Stop}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.abort_program(s, id); });
        return b;
    }));

    // 暂停 / 继续（2026-11-29 新增）：暂停 = 停止类（同中止，远程无控制权也可）；继续 = 控制类（远程要持有控制权）
    r.post(P + "/channels/{id}/program/pause", W({"program.pause", ActionClass::Stop}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.pause_program(s, id); });
        return b;
    }));

    r.post(P + "/channels/{id}/program/resume", W({"program.resume", ActionClass::Control}, [](const nlohmann::json*, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Bound b;
        b.exec = sync([id](Commands& c, const Source& s) { return c.resume_program(s, id); });
        return b;
    }));

    r.post(P + "/channels/{id}/program/copy", W({"program.copy", ActionClass::Control, true}, [self](const nlohmann::json* body, const Request& q, const Source&) {
        auto ch = channel_arg(q);
        if (!ch.id) return ch.reject;
        int id = *ch.id;
        Params p(body);
        auto to = p.int_list("to", true);
        std::vector<int> t = to.value_or(std::vector<int>{});
        return bad_or(p, [self, id, t](Commands& c, const Source& s, Done d) {
            auto r = c.copy_program(s, id, t);
            if (r.ok && r.data.is_object() && r.data.contains("to") && r.data["to"].is_array()) {
                std::lock_guard<std::mutex> lk(self->prog_mu_);
                auto src = self->prog_[static_cast<size_t>(id - 1)];
                for (const auto& x : r.data["to"])
                    if (x.is_number_integer() && x.get<int>() >= 1 && x.get<int>() <= 8) {
                        auto& pi = self->prog_[static_cast<size_t>(x.get<int>() - 1)];
                        int64_t rev = pi.revision;
                        pi = src;
                        pi.revision = rev + 1;
                        pi.modified_ms = self->now_wall();
                        pi.edited_not_sent = true;
                    }
                self->bump_programs();
            }
            d(r);
        });
    }));

    // —— 报警 ——
    r.post(P + "/alarms/{alarmId}/ack", W({"alarm.ack", ActionClass::Record}, [](const nlohmann::json*, const Request& q, const Source&) {
        std::string aid = q.param("alarmId");
        Bound b;
        b.exec = sync([aid](Commands& c, const Source& s) { return c.ack_alarm(s, aid); });
        return b;
    }));

    // —— 会话（Record 类，免租约） ——
    r.post(P + "/sessions", W({"session.begin", ActionClass::Record, true}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto tid = p.str("testId");
        if (!p.bad() && !tid) p.fail("缺少 testId");
        auto note = p.note("note");
        if (!note && !p.bad()) note = p.note("name");
        std::string t = tid.value_or("");
        return bad_or(p, sync([t, note](Commands& c, const Source& s) { return c.begin_session(s, t, note); }));
    }));
    r.del(P + "/sessions/current", W({"session.end", ActionClass::Record}, [](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = sync([](Commands& c, const Source& s) { return c.end_session(s); });
        return b;
    }));
    r.post(P + "/sessions/current/markers", W({"session.marker", ActionClass::Record, true}, [](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto text = p.str("text");
        if (!text && !p.bad()) text = p.str("label");
        if (!p.bad() && !text) p.fail("缺少 text");
        std::string t = text.value_or("");
        return bad_or(p, sync([t](Commands& c, const Source& s) { return c.add_marker(s, t); }));
    }));
}

}  // namespace reactor::api
