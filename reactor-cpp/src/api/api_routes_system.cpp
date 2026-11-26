// 网络（NetworkManager / nmcli）与 U 盘导出（接口契约 §3.14、§3.15），全部只在本机入口上开放。
// nmcli 一律经 run_process 以 argv 直接执行（不经 shell，SSID / 密码原样作为参数，不会被解释）；密码不进事件、日志、审计。
// 耗时的操作（扫描、连接、导出）回 202，在 net 的工作线程池里做完后记事件（network.* / export.*）并让 revs 加 1。
#include <unistd.h>

#include <algorithm>
#include <cstdio>
#include <fstream>

#include "api/api_routes.hpp"
#include "api/api_system.hpp"
#include "store/history_files.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace reactor::api {

namespace {

using net::Request;
using net::RouteOptions;
using cmd::ActionClass;
using cmd::Commands;
using cmd::Done;
using cmd::Source;
using ctl::OpResult;

std::vector<std::string> lines_of(const std::string& out) {
    std::vector<std::string> v;
    size_t pos = 0;
    while (pos < out.size()) {
        size_t e = out.find('\n', pos);
        if (e == std::string::npos) e = out.size();
        std::string l = out.substr(pos, e - pos);
        if (!l.empty() && l.back() == '\r') l.pop_back();
        if (!l.empty()) v.push_back(l);
        pos = e + 1;
    }
    return v;
}

bool valid_ipv4(const std::string& s) {
    int parts = 0;
    size_t pos = 0;
    while (pos <= s.size()) {
        size_t e = s.find('.', pos);
        if (e == std::string::npos) e = s.size();
        std::string p = s.substr(pos, e - pos);
        if (p.empty() || p.size() > 3 || !std::all_of(p.begin(), p.end(), [](char c) { return c >= '0' && c <= '9'; }) || std::stoi(p) > 255)
            return false;
        ++parts;
        pos = e + 1;
        if (e == s.size()) break;
    }
    return parts == 4;
}

struct NetState {
    bool available = false;
    std::string wifi_dev, wifi_con, eth_dev, eth_state;
};

}  // namespace

void Routes::system(ApiService& s, net::Router& r) {
    ApiService* self = &s;
    const std::string P = "/api/v1";
    RouteOptions local{net::RateClass::Auto, false, true};
    auto W = [self](WriteSpec spec, Binder b) { return [self, spec, b](const Request& q) { return self->command(q, spec, b); }; };
    auto nm = [self](std::vector<std::string> args, int64_t timeout_ms = 8000) {
        std::vector<std::string> argv = self->d_.nmcli;
        argv.insert(argv.end(), args.begin(), args.end());
        return run_process(argv, timeout_ms);
    };
    auto state = [nm]() {
        NetState st;
        auto r = nm({"-t", "-f", "DEVICE,TYPE,STATE,CONNECTION", "device", "status"}, 4000);
        if (!r.started || r.timed_out || r.exit_code != 0) return st;
        st.available = true;
        for (const auto& l : lines_of(r.out)) {
            auto f = nmcli_split(l);
            if (f.size() < 4) continue;
            if (f[1] == "wifi" && st.wifi_dev.empty()) {
                st.wifi_dev = f[0];
                if (f[2] == "connected") st.wifi_con = f[3];
            } else if (f[1] == "ethernet" && st.eth_dev.empty()) {
                st.eth_dev = f[0];
                st.eth_state = f[2];
            }
        }
        return st;
    };
    auto async = [self](std::function<void()> fn) {
        if (self->srv_) self->srv_->submit(std::move(fn));
        else fn();
    };
    auto event = [self](const std::string& level, const std::string& title, const std::string& msg, const std::string& kind) {
        if (self->d_.logger)
            self->d_.logger->event(store::WallTime::local(self->now_wall()), level, "系统", title, msg, "system", std::nullopt, std::nullopt, kind);
    };

    // —— GET /network ——
    r.get(P + "/network", [nm, state](const Request&) {
        auto st = state();
        char host[256] = {0};
        gethostname(host, sizeof host - 1);
        ojson j{{"wifiOn", false}, {"connectedSsid", nullptr}, {"ip", ""}, {"gateway", ""}, {"hostname", host}, {"strength", 0},
                {"autoConnect", true}, {"scanning", false}, {"dhcp", true}, {"ethernetPlugged", false}, {"ethernetIp", ""},
                {"nearby", ojson::array()}, {"available", st.available}};
        if (!st.available) return ok_json(j);
        auto radio = nm({"-t", "radio", "wifi"}, 3000);
        j["wifiOn"] = radio.exit_code == 0 && radio.out.find("enabled") == 0;
        auto ip_of = [&](const std::string& dev, std::string& ip, std::string* gw) {
            auto r = nm({"-t", "-f", "IP4.ADDRESS,IP4.GATEWAY", "device", "show", dev}, 3000);
            for (const auto& l : lines_of(r.out)) {
                auto f = nmcli_split(l);
                if (f.size() < 2) continue;
                if (f[0].rfind("IP4.ADDRESS", 0) == 0 && ip.empty()) ip = f[1].substr(0, f[1].find('/'));
                if (gw && f[0] == "IP4.GATEWAY") *gw = f[1];
            }
        };
        std::string ip, gw;
        if (!st.wifi_dev.empty()) ip_of(st.wifi_dev, ip, &gw);
        j["ip"] = ip;
        j["gateway"] = gw;
        if (!st.wifi_con.empty()) {
            j["connectedSsid"] = st.wifi_con;
            auto m = nm({"-g", "ipv4.method,connection.autoconnect", "connection", "show", st.wifi_con}, 3000);
            auto ls = lines_of(m.out);
            if (!ls.empty()) j["dhcp"] = ls[0] != "manual";
            if (ls.size() > 1) j["autoConnect"] = ls[1] == "yes";
        }
        if (!st.eth_dev.empty()) {
            j["ethernetPlugged"] = st.eth_state == "connected";
            std::string eip;
            ip_of(st.eth_dev, eip, nullptr);
            j["ethernetIp"] = eip;
        }
        std::vector<std::string> saved;
        auto sc = nm({"-t", "-f", "NAME,TYPE", "connection", "show"}, 3000);
        for (const auto& l : lines_of(sc.out)) {
            auto f = nmcli_split(l);
            if (f.size() >= 2 && f[1].find("wireless") != std::string::npos) saved.push_back(f[0]);
        }
        auto list = nm({"-t", "-f", "IN-USE,SSID,SIGNAL,SECURITY", "device", "wifi", "list", "--rescan", "no"}, 4000);
        ojson nearby = ojson::array();
        for (const auto& n : parse_wifi_list(list.out)) {
            int strength = n.signal >= 75 ? 4 : n.signal >= 50 ? 3 : n.signal >= 25 ? 2 : 1;
            if (n.in_use) j["strength"] = strength;
            nearby.push_back(ojson{{"ssid", n.ssid}, {"secured", !n.security.empty() && n.security != "--"}, {"strength", strength},
                                   {"saved", std::find(saved.begin(), saved.end(), n.ssid) != saved.end()}});
        }
        j["nearby"] = nearby;
        j["scanning"] = false;
        return ok_json(j);
    }, local);

    auto net_spec = [](const char* action) {
        WriteSpec w{action, ActionClass::Record};
        w.inline_exec = true;
        return w;
    };
    auto need_nm = [self, state](Done& d, NetState& st) {
        st = state();
        if (!st.available) {
            d(OpResult::fail("HW_NOT_CONFIGURED", "没有 NetworkManager（nmcli）· 无法设置网络"));
            return false;
        }
        if (self->net_busy_.load()) {
            d(OpResult::fail("NETWORK_BUSY", "正在扫描或连接 WiFi · 请稍候再试"));
            return false;
        }
        return true;
    };

    r.put(P + "/network/wifi", W(net_spec("network.wifi"), [self, nm, need_nm](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto en = p.boolean("enabled");
        if (!p.bad() && !en) p.fail("缺少 enabled（true / false）");
        if (p.bad()) return Bound::bad(p.error);
        bool on = *en;
        Bound b;
        b.exec = [self, nm, need_nm, on](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            auto r = nm({"radio", "wifi", on ? "on" : "off"});
            if (r.exit_code != 0) return d(OpResult::fail("HARDWARE_WRITE_FAILED", "WiFi 开关失败：" + trim(r.err)));
            self->bump_network();
            d(OpResult::done(on ? "WiFi 已打开" : "WiFi 已关闭", "ok", ojson{{"wifiOn", on}}));
        };
        return b;
    }), local);

    r.post(P + "/network/wifi/scan", W(net_spec("network.scan"), [self, nm, need_nm, async](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [self, nm, need_nm, async](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            self->net_busy_ = true;
            d(OpResult::accept("正在扫描 WiFi", "ok", ojson::object()));
            async([self, nm] {
                nm({"device", "wifi", "rescan"}, 15000);
                self->net_busy_ = false;
                self->bump_network();
            });
        };
        return b;
    }), local);

    WriteSpec conn = net_spec("network.connect");
    conn.redact_body = true;   // 正文里有密码：审计只记 SSID
    r.post(P + "/network/wifi/connect", W(conn, [self, nm, need_nm, async, event](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto ssid = p.str("ssid");
        auto pw = p.str("password");
        if (!p.bad() && (!ssid || ssid->empty())) p.fail("缺少 ssid");
        if (p.bad()) return Bound::bad(p.error);
        std::string sd = *ssid, pass = pw.value_or("");
        Bound b;
        b.exec = [self, nm, need_nm, async, event, sd, pass](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            self->net_busy_ = true;
            d(OpResult::accept("正在连接 " + sd, "ok", ojson::object()));
            async([self, nm, event, sd, pass] {
                std::vector<std::string> a = {"device", "wifi", "connect", sd};
                if (!pass.empty()) {
                    a.push_back("password");
                    a.push_back(pass);
                }
                auto r = nm(a, 45000);
                self->net_busy_ = false;
                self->bump_network();
                if (r.exit_code == 0) event("info", "WiFi 已连接", "已连上「" + sd + "」", "network.wifiConnected");
                else
                    event("warn", "WiFi 连接失败", "连不上「" + sd + "」" + (r.timed_out ? "（超时）" : "") + " · 请检查密码和信号",
                          "network.wifiFailed");
            });
        };
        return b;
    }), local);

    r.del(P + "/network/wifi/saved/{ssid}", W(net_spec("network.forget"), [self, nm, need_nm](const nlohmann::json*, const Request& q, const Source&) {
        std::string ssid = q.param("ssid");
        Bound b;
        b.exec = [self, nm, need_nm, ssid](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            auto r = nm({"connection", "delete", "id", ssid});
            if (r.exit_code != 0) return d(OpResult::fail("NOT_FOUND", "没有这个已保存的网络：" + ssid));
            self->bump_network();
            d(OpResult::done("已忘记「" + ssid + "」", "ok", ojson::object()));
        };
        return b;
    }), local);

    r.post(P + "/network/wifi/disconnect", W(net_spec("network.disconnect"), [self, nm, need_nm](const nlohmann::json*, const Request&, const Source&) {
        Bound b;
        b.exec = [self, nm, need_nm](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            if (st.wifi_dev.empty()) return d(OpResult::fail("HW_NOT_CONFIGURED", "没有 WiFi 网卡"));
            nm({"device", "disconnect", st.wifi_dev});
            self->bump_network();
            d(OpResult::done("WiFi 已断开", "ok", ojson::object()));
        };
        return b;
    }), local);

    r.put(P + "/network/ip", W(net_spec("network.ip"), [self, nm, need_nm](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto mode = p.str("mode");
        auto addr = p.str("address");
        auto prefix = p.opt_int("prefix");
        auto gw = p.str("gateway");
        if (!p.bad() && (!mode || (*mode != "dhcp" && *mode != "manual"))) p.fail("mode 只能是 dhcp / manual");
        if (p.bad()) return Bound::bad(p.error);
        std::vector<std::string> dns;
        if (auto d = p.prop("dns"); d && d->is_array())
            for (const auto& x : *d)
                if (x.is_string()) dns.push_back(x.get<std::string>());
        bool manual = *mode == "manual";
        if (manual) {
            if (!addr || !valid_ipv4(*addr)) return Bound::reject("PARAM_OUT_OF_RANGE", "IP 地址写法不对（例：192.168.1.50）");
            if (!prefix || *prefix < 1 || *prefix > 32) return Bound::reject("PARAM_OUT_OF_RANGE", "子网前缀须在 1–32 之间");
            if (gw && !gw->empty() && !valid_ipv4(*gw)) return Bound::reject("PARAM_OUT_OF_RANGE", "网关写法不对（例：192.168.1.1）");
            for (const auto& x : dns)
                if (!valid_ipv4(x)) return Bound::reject("PARAM_OUT_OF_RANGE", "DNS 写法不对：" + x);
        }
        std::string a = addr.value_or(""), g = gw.value_or("");
        int pf = prefix.value_or(24);
        Bound b;
        b.exec = [self, nm, need_nm, manual, a, pf, g, dns](Commands&, const Source&, Done d) {
            NetState st;
            if (!need_nm(d, st)) return;
            std::string con = !st.wifi_con.empty() ? st.wifi_con : std::string();
            if (con.empty()) return d(OpResult::fail("NOT_FOUND", "现在没有连着的网络 · 先连上 WiFi 再改 IP"));
            std::vector<std::string> args = {"connection", "modify", con};
            if (manual) {
                std::string dl;
                for (const auto& x : dns) dl += (dl.empty() ? "" : " ") + x;
                args.insert(args.end(), {"ipv4.method", "manual", "ipv4.addresses", a + "/" + std::to_string(pf), "ipv4.gateway", g, "ipv4.dns", dl});
            } else {
                args.insert(args.end(), {"ipv4.method", "auto", "ipv4.addresses", "", "ipv4.gateway", "", "ipv4.dns", ""});
            }
            auto r = nm(args);
            if (r.exit_code != 0) return d(OpResult::fail("HARDWARE_WRITE_FAILED", "改 IP 失败：" + trim(r.err)));
            nm({"connection", "up", con}, 20000);
            self->bump_network();
            d(OpResult::done(manual ? "已改为固定 IP " + a : "已改为自动获取 IP（DHCP）", "ok", ojson{{"dhcp", !manual}}));
        };
        return b;
    }), local);

    // —— GET /usb ——
    r.get(P + "/usb", [self](const Request&) {
        auto vols = list_usb(self->d_.usb_root);
        std::string last;
        {
            std::lock_guard<std::mutex> lk(self->misc_mu_);
            last = self->last_export_;
        }
        if (vols.empty()) return ok_json(ojson{{"present", false}, {"label", nullptr}, {"freeBytes", nullptr}, {"lastExport", last.empty() ? ojson(nullptr) : ojson(last)}});
        return ok_json(ojson{{"present", true}, {"label", vols[0].label}, {"freeBytes", vols[0].free_bytes},
                             {"lastExport", last.empty() ? ojson(nullptr) : ojson(last)}});
    }, local);

    // —— POST /export（本地，R，幂等必填，202） ——
    WriteSpec ex{"export", ActionClass::Record, true};
    ex.inline_exec = true;
    r.post(P + "/export", W(ex, [self, async, event](const nlohmann::json* body, const Request&, const Source&) {
        Params p(body);
        auto run_id = p.str("runId");
        auto file = p.str("file");
        auto day = p.str("day");
        auto from_s = p.str("from");
        auto to_s = p.str("to");
        std::vector<std::string> kinds;
        if (auto k = p.prop("kinds"); k && k->is_array())
            for (const auto& x : *k)
                if (x.is_string()) kinds.push_back(x.get<std::string>());
        auto step = p.num("stepSec");
        if (p.bad()) return Bound::bad(p.error);
        if (std::find(kinds.begin(), kinds.end(), "pdf") != kinds.end())
            return Bound::reject("PARAM_OUT_OF_RANGE", "暂不支持导出 PDF · 请只选 CSV / 事件 / 程序");
        int64_t f = 0, t = 0;
        if (!run_id && !file && !day) {
            if (!from_s || !to_s || !store::history_files::try_parse_query_time(*from_s, f) || !store::history_files::try_parse_query_time(*to_s, t) ||
                t <= f)
                return Bound::bad("要导出什么？runId / file / day / from+to 四选一（时间要带时区）");
        }
        if (day && (day->size() != 8 || !std::all_of(day->begin(), day->end(), [](char c) { return c >= '0' && c <= '9'; })))
            return Bound::bad("day 写成 yyyyMMdd");
        int64_t step_ms = step ? static_cast<int64_t>(*step * 1000) : 0;
        Bound b;
        b.exec = [self, async, event, run_id, file, day, f, t, kinds, step_ms](Commands&, const Source&, Done d) {
            auto vols = list_usb(self->d_.usb_root);
            if (vols.empty()) return d(OpResult::fail("USB_NOT_PRESENT", "没有检测到 U 盘 · 请插好后再导出"));
            if (self->export_busy_.load()) return d(OpResult::fail("EXPORT_BUSY", "上一个导出还没完成 · 请稍候"));
            // 要导出的内容：(目标文件名, 来源路径 或 时间段)
            struct Job {
                std::string name, src;
                int64_t from = 0, to = 0;
                bool range = false;
                std::string content;
                Job(std::string n, std::string s2, int64_t a = 0, int64_t z = 0, bool rg = false, std::string c = "")
                    : name(std::move(n)), src(std::move(s2)), from(a), to(z), range(rg), content(std::move(c)) {}
            };
            std::vector<Job> jobs;
            std::string what;
            auto has = [&](const char* k) { return kinds.empty() || std::find(kinds.begin(), kinds.end(), k) != kinds.end(); };
            auto days_files = [&](const char* type, int64_t a, int64_t z) {
                for (const auto& path : store::history_files::select_daily_files(self->d_.data_dir, a, z, type))
                    jobs.push_back(Job{store::base_name(path), path});
            };
            if (run_id) {
                what = *run_id;
                if (store::safe_path::is_leak_run_id(*run_id)) {
                    for (const char* suf : {".csv", "-summary.csv", "-summary.json"})
                        if (auto pth = store::safe_path::try_resolve(self->d_.data_dir, "leak", *run_id + suf); pth && store::file_exists(*pth))
                            jobs.push_back(Job{*run_id + suf, *pth});
                    if (jobs.empty()) return d(OpResult::fail("NOT_FOUND", "运行记录 " + *run_id + " 不存在"));
                } else {
                    auto rg = self->runs_->range(*run_id, self->now_wall());
                    if (!rg) return d(OpResult::fail("NOT_FOUND", "运行记录 " + *run_id + " 不存在"));
                    if (has("csv")) jobs.push_back(Job{*run_id + "-telemetry.csv", "", rg->start, rg->end + 1, true});
                    if (has("events")) days_files("events", rg->start, rg->end + 1);
                    if (has("program"))
                        if (auto rj = self->runs_->get(*run_id)) jobs.push_back(Job{*run_id + "-program.json", "", 0, 0, false, dump((*rj)["program"])});
                }
            } else if (file) {
                what = *file;
                std::string type = file->rfind("events", 0) == 0 ? "events" : "telemetry";
                auto pth = store::safe_path::try_resolve(self->d_.data_dir, type, *file);
                if (!pth || !store::file_exists(*pth)) return d(OpResult::fail("NOT_FOUND", "文件 " + *file + " 不存在"));
                jobs.push_back(Job{*file, *pth});
            } else if (day) {
                what = *day;
                for (const char* type : {"telemetry", "events"})
                    for (const auto& fi : store::history_files::list(self->d_.data_dir, type, false)) {
                        auto dd = store::history_files::day_of(fi.name);
                        if (dd && dd->first == *day)
                            if (auto pth = store::safe_path::try_resolve(self->d_.data_dir, type, fi.name)) jobs.push_back(Job{fi.name, *pth});
                    }
                if (jobs.empty()) return d(OpResult::fail("NOT_FOUND", *day + " 没有数据文件"));
            } else {
                what = utc_iso(f) + " – " + utc_iso(t);
                if (has("telemetry")) jobs.push_back(Job{"telemetry-range.csv", "", f, t, true});
                if (has("events")) days_files("events", f, t);
            }
            std::string export_id;
            {
                std::lock_guard<std::mutex> lk(self->misc_mu_);
                export_id = "exp-" + store::fmt_wall(store::WallTime::local(self->now_wall()), "yyyyMMdd-HHmmss");
                if (++self->export_seq_ > 1) export_id += "-" + std::to_string(self->export_seq_);
            }
            std::string rel = "PR8/" + store::fmt_wall(store::WallTime::local(self->now_wall()), "yyyyMMdd");
            std::string dir = store::join_path(vols[0].path, rel);
            self->export_busy_ = true;
            d(OpResult::accept("正在导出到 U 盘", "ok", ojson{{"exportId", export_id}}));
            async([self, event, jobs, dir, rel, what, step_ms] {
                std::string err;
                int64_t bytes = 0;
                if (!store::make_dirs(dir, &err)) err = "建不了目录 " + rel;
                for (const auto& j : jobs) {
                    if (!err.empty()) break;
                    std::string out_path = store::join_path(dir, j.name);
                    std::ofstream out(out_path, std::ios::binary | std::ios::trunc);
                    if (!out) { err = "写不了 " + j.name; break; }
                    if (!j.content.empty()) {
                        out << j.content;
                    } else if (j.range) {
                        auto files = store::history_files::select_daily_files(self->d_.data_dir, j.from, j.to);
                        int64_t next = 0;
                        bool first = true;
                        store::history_files::write_range(files, j.from, j.to, [&](const std::string& chunk) {
                            // write_range 按行给（先 BOM + 表头）；stepSec > 0 时按时间抽稀
                            if (step_ms <= 0 || first) {
                                first = false;
                                out << chunk;
                                return static_cast<bool>(out);
                            }
                            auto ts = store::history_files::parse_row_time(chunk);
                            if (!ts) { out << chunk; return static_cast<bool>(out); }
                            if (*ts >= next) {
                                out << chunk;
                                next = (*ts / step_ms + 1) * step_ms;
                            }
                            return static_cast<bool>(out);
                        });
                    } else {
                        std::ifstream in(j.src, std::ios::binary);
                        out << in.rdbuf();
                    }
                    out.flush();
                    if (!out) { err = "写 " + j.name + " 失败（U 盘满了或被拔出）"; break; }
                    bytes += store::stat_path(out_path).size;
                }
                ::sync();
                self->export_busy_ = false;
                {
                    std::lock_guard<std::mutex> lk(self->misc_mu_);
                    if (err.empty()) self->last_export_ = what;
                }
                self->bump_usb();
                if (err.empty())
                    event("info", "导出完成", "已导出到 U 盘：" + rel + "/（" + std::to_string(jobs.size()) + " 个文件，" +
                                                  std::to_string(bytes / 1024) + " KB）",
                          "export.done");
                else event("warn", "导出失败", err, "export.failed");
            });
        };
        return b;
    }), local);
}

}  // namespace reactor::api
