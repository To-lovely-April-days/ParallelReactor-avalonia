// 写命令幂等表：(令牌名, Idempotency-Key) → (请求哈希, 状态, 响应)。TTL 10 min、容量 1000、只在内存。
// C#: Api/IdempotencyStore.cs + Endpoints.Write.cs CommandAsync 第 3 步。线程安全，时间由调用方传入（单调毫秒）。
#pragma once
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#include <utility>

namespace reactor::net {

inline constexpr int64_t kIdemTtlMs = 10 * 60 * 1000;
inline constexpr size_t kIdemCapacity = 1000;
inline constexpr size_t kIdemKeyMaxLen = 128;
inline constexpr int64_t kIdemInProgressWaitMs = 5000;

enum class IdemBegin { New, Replay, Mismatch, InProgress };

struct IdemEntry {
    std::string key, body_hash, op_id;
    int64_t created_ms = 0;
    // 以下由 store 的锁保护；用 IdempotencyStore::snapshot / wait 读
    bool done = false;
    bool abandoned = false;
    int status = 0;
    std::string body;
    std::vector<std::pair<std::string, std::string>> headers;   // 重放时一并带回（如 Content-Type）
};
using IdemEntryPtr = std::shared_ptr<IdemEntry>;

class IdempotencyStore {
public:
    // 表内键：令牌名 + '\n' + key（按令牌隔离）
    static std::string scoped_key(const std::string& token_name, const std::string& key);
    // 请求哈希：SHA-256(方法 + '\n' + 路径 + '\n' + 正文) 小写十六进制
    static std::string hash(const std::string& method, const std::string& path, const std::string& body);

    // 登记 / 查询（C#: Begin）。New 时 entry 为新建的进行中条目，调用方执行后必须 complete 或 abandon。
    IdemBegin begin(const std::string& scoped, const std::string& body_hash, const std::string& op_id,
                    int64_t now_ms, IdemEntryPtr& entry);
    void complete(const IdemEntryPtr& e, int status, std::string body,
                  std::vector<std::pair<std::string, std::string>> headers = {});
    // 撤销（只有新建者能调）：同键重发可重新执行；唤醒等待方让它们重新 begin
    void abandon(const IdemEntryPtr& e);
    // 等进行中的条目结束（完成或撤销），最多 timeout_ms；返回 true = 已结束（看 e->abandoned 区分）
    bool wait(const IdemEntryPtr& e, int64_t timeout_ms);
    size_t size();

    // 一站式：处理 begin + 进行中等待 + 撤销重登记（C#: CommandAsync 第 3 步的 while 循环）。
    // 结果 kind：New（调用方执行并 complete/abandon）、Replay（entry 已完成，回放 status/body，头 Idempotent-Replay: true）、
    // Mismatch（409 IDEMPOTENCY_MISMATCH）、InProgress（等满 wait_ms 仍未完成 → 409 IDEMPOTENCY_IN_PROGRESS，op_id 用 entry->op_id）。
    // now_fn 给出当前单调毫秒。会阻塞调用线程最多 wait_ms（在工作线程上调用，不要在 I/O 线程上调）。
    struct Acquire { IdemBegin kind; IdemEntryPtr entry; };
    Acquire acquire(const std::string& scoped, const std::string& body_hash, const std::string& op_id,
                    int64_t now_ms, int64_t wait_ms = kIdemInProgressWaitMs);

private:
    void trim_locked(int64_t now_ms);
    std::mutex mu_;
    std::condition_variable cv_;
    std::unordered_map<std::string, IdemEntryPtr> m_;
};

}  // namespace reactor::net
