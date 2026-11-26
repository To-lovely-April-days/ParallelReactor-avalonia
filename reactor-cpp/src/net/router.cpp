#include "net/router.hpp"

#include <algorithm>
#include <cctype>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace reactor::net {

namespace {

std::vector<std::string> split_path(const std::string& p) {
    std::vector<std::string> out;
    size_t i = 0;
    while (i < p.size()) {
        if (p[i] == '/') { ++i; continue; }
        size_t j = p.find('/', i);
        if (j == std::string::npos) j = p.size();
        out.push_back(p.substr(i, j - i));
        i = j;
    }
    return out;
}

std::string upper(std::string s) {
    for (auto& c : s) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return s;
}

}  // namespace

RateClass effective_rate_class(RateClass c, const std::string& method) {
    if (c != RateClass::Auto) return c;
    return (method == "GET" || method == "HEAD") ? RateClass::Read : RateClass::Write;
}

Router& Router::add(const std::string& method, const std::string& pattern, Handler h, RouteOptions o) {
    Route r;
    r.method = upper(method);
    r.pattern = pattern;
    for (auto& s : split_path(pattern)) {
        Route::Seg seg;
        if (s.size() >= 2 && s.front() == '{' && s.back() == '}') {
            seg.param = true;
            seg.text = s.substr(1, s.size() - 2);
            if (!seg.text.empty() && seg.text[0] == '*') { seg.catch_all = true; seg.text = seg.text.substr(1); }
        } else {
            seg.text = s;
            ++r.literal_count;
        }
        r.segs.push_back(std::move(seg));
    }
    r.handler = std::move(h);
    r.opts = o;
    routes_.push_back(std::move(r));
    return *this;
}

Router& Router::add_sync(const std::string& method, const std::string& pattern, SyncHandler h, RouteOptions o) {
    auto fn = std::make_shared<SyncHandler>(std::move(h));
    return add(method, pattern, [fn](const Request& req, Responder res) { res.send((*fn)(req)); }, o);
}

Router& Router::ws(const std::string& pattern, WsHandlers h, RouteOptions o) {
    add("GET", pattern, Handler{}, o);
    routes_.back().ws = std::make_shared<WsHandlers>(std::move(h));
    return *this;
}

Router::Match Router::match(const std::string& method_in, const std::string& raw_path, bool is_local) const {
    Match m;
    std::string method = upper(method_in);
    auto raw = split_path(raw_path);
    std::vector<std::string> segs;
    segs.reserve(raw.size());
    for (auto& s : raw) segs.push_back(url_decode(s, false));

    const Route* best = nullptr;
    int best_score = -1;
    std::unordered_map<std::string, std::string> best_params;
    std::vector<std::string> allow;
    for (auto& r : routes_) {
        if (r.opts.local_only && !is_local) continue;
        std::unordered_map<std::string, std::string> params;
        bool ok = true;
        size_t i = 0;
        for (; i < r.segs.size() && ok; ++i) {
            auto& sg = r.segs[i];
            if (sg.catch_all) {
                std::string rest;
                for (size_t k = i; k < segs.size(); ++k) rest += (k > i ? "/" : "") + segs[k];
                params[sg.text] = rest;
                i = segs.size();
                break;
            }
            if (i >= segs.size()) { ok = false; break; }
            if (sg.param) {
                if (segs[i].empty()) ok = false;
                else params[sg.text] = segs[i];
            } else if (!iequals(sg.text, segs[i])) {
                ok = false;
            }
        }
        if (!ok) continue;
        bool catch_all = !r.segs.empty() && r.segs.back().catch_all;
        if (!catch_all && i != segs.size()) continue;
        if (!catch_all && r.segs.size() != segs.size()) continue;
        bool method_ok = r.method == method || (method == "HEAD" && r.method == "GET");
        if (!method_ok) {
            if (std::find(allow.begin(), allow.end(), r.method) == allow.end()) allow.push_back(r.method);
            continue;
        }
        int score = r.literal_count * 2 + (catch_all ? 0 : 1);
        if (score > best_score) {
            best = &r;
            best_score = score;
            best_params = std::move(params);
        }
    }
    if (best) {
        m.route = best;
        m.params = std::move(best_params);
        return m;
    }
    if (!allow.empty()) {
        m.method_mismatch = true;
        auto g = std::find(allow.begin(), allow.end(), "GET");
        if (g != allow.end()) allow.insert(g + 1, "HEAD");
        for (size_t k = 0; k < allow.size(); ++k) m.allow += (k ? ", " : "") + allow[k];
    }
    return m;
}

}  // namespace reactor::net
