#include "core/gas_lease.hpp"
#include "core/chan_name.hpp"

#include <algorithm>

namespace reactor::core {

namespace {
std::vector<int> distinct(const std::vector<int>& v) {
    std::vector<int> out;
    for (int x : v)
        if (std::find(out.begin(), out.end(), x) == out.end()) out.push_back(x);
    return out;
}
}  // namespace

// ---------------- GasLease ----------------

int64_t GasLease::begin(GasOwner owner, const std::string& task, const std::vector<int>& participants,
                        const std::vector<int>& watch, const std::map<int, ReactorState>& start_states,
                        std::optional<int> owner_rv) {
    if (owner == GasOwner::None) return 0;
    bool renew = owner == owner_ && (owner != GasOwner::Program || owner_rv == owner_rv_);
    std::vector<int> ids = distinct(participants);
    std::map<int, ReactorState> start = start_states;
    if (renew) {
        for (int id : ids) {
            auto it = start_.find(id);
            if (it != start_.end()) start[id] = it->second;
        }
        std::set<int> keep;
        for (int id : ids)
            if (invalid_.count(id)) keep.insert(id);
        invalid_.swap(keep);
    } else {
        invalid_.clear();
    }
    owner_ = owner;
    owner_rv_ = owner == GasOwner::Program ? owner_rv : std::nullopt;
    task_ = task;
    participants_ = ids;
    watch_ = distinct(watch);
    start_ = std::move(start);
    return ++generation_;
}

bool GasLease::can_abort(GasOwner requester, std::optional<int> rv) const {
    return requester != GasOwner::None && requester == owner_ && (!rv || owner_rv_ == rv);
}

bool GasLease::try_end(GasOwner owner, int64_t gen) {
    if (owner == GasOwner::None || owner != owner_) return false;
    if (gen != 0 && gen != generation_) return false;
    owner_ = GasOwner::None;
    owner_rv_.reset();
    task_.clear();
    participants_.clear();
    watch_.clear();
    start_.clear();
    invalid_.clear();
    return true;
}

bool GasLease::includes(int id) const {
    return busy() && std::find(participants_.begin(), participants_.end(), id) != participants_.end();
}

void GasLease::invalidate(int id) {
    if (includes(id)) invalid_.insert(id);
}

std::optional<std::string> GasLease::fault_reason(
    const std::function<std::optional<ReactorState>(int)>& state_of) const {
    if (!busy()) return std::nullopt;
    for (int id : participants_) {
        std::string rv = chan_name(id);
        if (invalid_.count(id)) return rv + " 已被停止 / 停用，序列中止";
        auto st = state_of(id);
        if (!st) continue;
        if (*st == ReactorState::Alarm) return rv + " 报警，序列中止";
        if (*st == ReactorState::Idle || *st == ReactorState::Done) {
            auto it = start_.find(id);
            if (it == start_.end() || it->second != *st)
                return *st == ReactorState::Idle ? rv + " 已被停用，序列中止" : rv + " 已被停止，序列中止";
        }
    }
    return std::nullopt;
}

// ---------------- GasQueue ----------------

int64_t GasQueue::enqueue(int rv, int64_t now_ms) {
    for (auto& e : q_)
        if (e.rv == rv) return e.ticket;
    int64_t t = ++next_;
    q_.push_back({t, rv, now_ms});
    return t;
}

bool GasQueue::remove(int64_t ticket) {
    auto it = std::find_if(q_.begin(), q_.end(), [&](const Entry& e) { return e.ticket == ticket; });
    if (it == q_.end()) return false;
    q_.erase(it);
    return true;
}

bool GasQueue::contains(int rv) const {
    return std::any_of(q_.begin(), q_.end(), [&](const Entry& e) { return e.rv == rv; });
}

int64_t GasQueue::waited_ms(int64_t ticket, int64_t now_ms) const {
    auto it = std::find_if(q_.begin(), q_.end(), [&](const Entry& e) { return e.ticket == ticket; });
    return it == q_.end() ? 0 : now_ms - it->since;
}

bool GasQueue::timed_out(int64_t ticket, int64_t now_ms, int64_t max_ms) const {
    return max_ms > 0 && waited_ms(ticket, now_ms) >= max_ms;
}

std::vector<int> GasQueue::waiting() const {
    std::vector<int> v;
    for (auto& e : q_) v.push_back(e.rv);
    return v;
}

}  // namespace reactor::core
