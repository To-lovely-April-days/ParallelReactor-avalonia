#include "ctl/ctl_alarm_table.hpp"

#include "store/event_store.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <iterator>
#include <string>
#include <vector>

namespace reactor::ctl {

namespace al = core::alarm_lifecycle;

int alarm_ui_level(const std::string& key, const std::string& level) {
    auto starts = [&](const char* p) { return key.rfind(p, 0) == 0; };
    if (starts("ot:") || starts("op:") || starts("opv:") || starts("oph:")) return 1;
    if (level == "err") return 2;
    if (level == "warn") return 3;
    return 0;
}

nlohmann::ordered_json alarm_to_json(const AlarmItem& a) {
    nlohmann::ordered_json j;
    j["id"] = a.id;
    j["level"] = a.level;
    j["rv"] = a.rv;
    j["channel"] = a.channel ? nlohmann::ordered_json(*a.channel) : nlohmann::ordered_json(nullptr);
    j["title"] = a.title;
    j["message"] = a.message;
    j["raisedAt"] = store::fmt_utc_json(a.raised_at_ms);
    j["state"] = a.state;
    j["clearedAt"] = a.cleared_at_ms ? nlohmann::ordered_json(store::fmt_utc_json(*a.cleared_at_ms)) : nlohmann::ordered_json(nullptr);
    j["uiLevel"] = a.ui_level;
    return j;
}

const AlarmItem& AlarmTable::add(const std::string& key, const std::string& level, const std::string& rv,
                                 const std::string& title, const std::string& detail, const std::string& kind) {
    if (sink_) sink_(level, rv, title, detail, kind);
    AlarmItem it;
    it.id = std::to_string(++seq_);
    it.level = level;
    it.ui_level = alarm_ui_level(key, level);
    it.rv = rv;
    it.channel = store::event_kinds::channel_of(rv, title);
    it.title = title;
    it.message = detail;
    it.raised_at_ms = now_();
    it.state = level == "info" ? al::kDone : al::kActive;
    it.key = key;
    it.kind = kind;
    items_.push_front(std::move(it));
    if (!key.empty() && level != "info") index_.emplace_back(key, items_.front().id);
    trim();
    if (on_change) on_change(items_.front());
    return items_.front();
}

const AlarmItem& AlarmTable::log(const std::string& level, const std::string& rv, const std::string& title,
                                 const std::string& detail, const std::string& kind) {
    return add("", level, rv, title, detail, kind);
}

const AlarmItem& AlarmTable::raise(const std::string& key, const std::string& level, const std::string& rv,
                                   const std::string& title, const std::string& detail, const std::string& kind) {
    return add(key, level, rv, title, detail, kind);
}

bool AlarmTable::has_active(const std::string& key) const {
    for (const auto& kv : index_)
        if (kv.first == key) return true;
    return false;
}

int AlarmTable::clear(const std::string& key, const std::string& rv, const std::string& title, const std::string& detail,
                      const std::string& kind) {
    int n = 0;
    int64_t now = now_();
    for (auto it = index_.begin(); it != index_.end();) {
        if (it->first != key) { ++it; continue; }
        if (AlarmItem* a = find_mut(it->second)) {
            std::string next = al::on_cleared(a->state);
            if (next != a->state) {
                a->state = next;
                a->cleared_at_ms = now;
                ++n;
                if (on_change) on_change(*a);
            }
        }
        it = index_.erase(it);
    }
    log("info", rv, title, n > 0 ? detail + " · 自动解除 " + std::to_string(n) + " 条报警" : detail, kind);
    return n;
}

bool AlarmTable::ack(const std::string& id) {
    AlarmItem* a = find_mut(id);
    if (!a) return false;
    a->state = al::on_ack(a->state);
    if (on_change) on_change(*a);
    return true;
}

int AlarmTable::ack_all() {
    int n = 0;
    for (auto& a : items_) {
        std::string next = al::on_ack(a.state);
        if (next == a.state) continue;
        a.state = next;
        ++n;
        if (on_change) on_change(a);
    }
    return n;
}

const AlarmItem* AlarmTable::find(const std::string& id) const {
    for (const auto& a : items_)
        if (a.id == id) return &a;
    return nullptr;
}

AlarmItem* AlarmTable::find_mut(const std::string& id) {
    for (auto& a : items_)
        if (a.id == id) return &a;
    return nullptr;
}

std::vector<AlarmItem> AlarmTable::list(const std::string& f) const {
    std::vector<AlarmItem> out;
    for (const auto& a : items_)
        if (f.empty() || f == "all" || a.state == f) out.push_back(a);
    return out;
}

int AlarmTable::active_count() const {
    int n = 0;
    for (const auto& a : items_)
        if (a.state == al::kActive) ++n;
    return n;
}

void AlarmTable::trim() {
    while (items_.size() > kMaxItems) {
        // 从最旧的一端找第一条非活动条目丢掉；全是活动条目时丢最旧的
        auto victim = items_.end();
        for (auto it = items_.end(); it != items_.begin();) {
            --it;
            if (it->state == al::kDone || it->state == al::kCleared) { victim = it; break; }
        }
        if (victim == items_.end()) victim = std::prev(items_.end());
        std::string id = victim->id;
        items_.erase(victim);
        for (auto it = index_.begin(); it != index_.end();)
            it = it->second == id ? index_.erase(it) : std::next(it);
    }
}

}  // namespace reactor::ctl
