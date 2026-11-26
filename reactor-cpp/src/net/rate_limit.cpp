#include "net/rate_limit.hpp"

namespace reactor::net {

bool FixedWindowLimiter::try_acquire(const std::string& key, int64_t now_ms) {
    std::lock_guard<std::mutex> lk(mu_);
    auto& e = m_[key];
    if (e.second == 0 || now_ms - e.first >= window_ms_) {
        e.first = now_ms;
        e.second = 0;
    }
    bool ok = e.second < permits_;
    if (ok) ++e.second;
    if (m_.size() > 1000)
        for (auto i = m_.begin(); i != m_.end();)
            i = now_ms - i->second.first > window_ms_ * 2 ? m_.erase(i) : std::next(i);
    return ok;
}

std::shared_ptr<void> ConcurrencyLimiter::try_acquire() {
    int cur = used_->load();
    do {
        if (cur >= permits_) return nullptr;
    } while (!used_->compare_exchange_weak(cur, cur + 1));
    auto used = used_;
    // 自定义删除器归还许可（指针本身不指向任何东西）
    return std::shared_ptr<void>(static_cast<void*>(used.get()), [used](void*) { used->fetch_sub(1); });
}

}  // namespace reactor::net
