#include "net/idempotency.hpp"

#include <algorithm>
#include <chrono>

#include "net/crypto.hpp"
#include <cstdint>
#include <iterator>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace reactor::net {

std::string IdempotencyStore::scoped_key(const std::string& token_name, const std::string& key) {
    return token_name + "\n" + key;
}

std::string IdempotencyStore::hash(const std::string& method, const std::string& path, const std::string& body) {
    return sha256_hex(method + "\n" + path + "\n" + body);
}

void IdempotencyStore::trim_locked(int64_t now_ms) {
    if (m_.size() < kIdemCapacity) return;
    for (auto i = m_.begin(); i != m_.end();)
        i = now_ms - i->second->created_ms > kIdemTtlMs ? m_.erase(i) : std::next(i);
    if (m_.size() < kIdemCapacity) return;
    // 淘汰最旧的，直到留出一个空位
    std::vector<std::pair<int64_t, std::string>> order;
    order.reserve(m_.size());
    for (auto& kv : m_) order.emplace_back(kv.second->created_ms, kv.first);
    std::sort(order.begin(), order.end());
    size_t drop = m_.size() - kIdemCapacity + 1;
    for (size_t i = 0; i < drop && i < order.size(); ++i) m_.erase(order[i].second);
}

IdemBegin IdempotencyStore::begin(const std::string& scoped, const std::string& body_hash, const std::string& op_id,
                                  int64_t now_ms, IdemEntryPtr& entry) {
    std::lock_guard<std::mutex> lk(mu_);
    trim_locked(now_ms);
    auto it = m_.find(scoped);
    if (it != m_.end() && now_ms - it->second->created_ms > kIdemTtlMs) {   // 过期：当新键
        m_.erase(it);
        it = m_.end();
    }
    if (it != m_.end()) {
        entry = it->second;
        if (entry->body_hash != body_hash) return IdemBegin::Mismatch;
        return entry->done ? IdemBegin::Replay : IdemBegin::InProgress;
    }
    auto e = std::make_shared<IdemEntry>();
    e->key = scoped;
    e->body_hash = body_hash;
    e->op_id = op_id;
    e->created_ms = now_ms;
    m_[scoped] = e;
    entry = e;
    return IdemBegin::New;
}

void IdempotencyStore::complete(const IdemEntryPtr& e, int status, std::string body,
                                std::vector<std::pair<std::string, std::string>> headers) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        e->status = status;
        e->body = std::move(body);
        e->headers = std::move(headers);
        e->done = true;
    }
    cv_.notify_all();
}

void IdempotencyStore::abandon(const IdemEntryPtr& e) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        auto it = m_.find(e->key);
        if (it != m_.end() && it->second == e) m_.erase(it);
        e->abandoned = true;
    }
    cv_.notify_all();
}

bool IdempotencyStore::wait(const IdemEntryPtr& e, int64_t timeout_ms) {
    std::unique_lock<std::mutex> lk(mu_);
    return cv_.wait_for(lk, std::chrono::milliseconds(timeout_ms), [&] { return e->done || e->abandoned; });
}

size_t IdempotencyStore::size() {
    std::lock_guard<std::mutex> lk(mu_);
    return m_.size();
}

IdempotencyStore::Acquire IdempotencyStore::acquire(const std::string& scoped, const std::string& body_hash,
                                                    const std::string& op_id, int64_t now_ms, int64_t wait_ms) {
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(wait_ms);
    while (true) {
        IdemEntryPtr e;
        IdemBegin b = begin(scoped, body_hash, op_id, now_ms, e);
        if (b != IdemBegin::InProgress) return {b, e};
        auto left = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - std::chrono::steady_clock::now()).count();
        if (left <= 0 || !wait(e, left)) return {IdemBegin::InProgress, e};
        std::lock_guard<std::mutex> lk(mu_);
        if (e->abandoned) continue;          // 原请求撤销了：重新登记（可能轮到自己执行）
        return {IdemBegin::Replay, e};
    }
}

}  // namespace reactor::net
