// 运行记录（接口契约 §3.8，新增）：一次「运行」= 一次程序运行（同时开始的几路算一次，开始时刻相差 ≤ 10 s）或一次泄漏测试。
// 程序运行由中枢线程每帧看快照 program_runs[] 跟踪（开始 / 结束），步骤用时由程序引擎的 on_step 回调（控制线程）记；
// 每条记录存 <data>/runs/<id>.json（重启后还在）。泄漏测试的记录直接从 leak/*-summary.json 读。
// 线程：内部一把锁，任意线程可调。
#pragma once
#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "ctl/ctl_snapshot.hpp"

namespace reactor::api {

using ctl::ojson;

class RunsBook {
public:
    explicit RunsBook(std::string data_dir);

    struct Limits {
        double over_pressure = 510, over_temp = 250, full_scale_psi = 600;
    };
    // 中枢线程，每帧：返回运行记录是否有变化（revs.runs + 1）
    bool track(const ctl::Snapshot& s, const Limits& lim, int64_t now_ms);
    // 控制线程：程序步骤状态变化（记每步用时、开始充反应气的时刻）
    void on_step(int rv, int index, const std::string& state, int64_t now_ms);

    ojson list(int limit) const;                         // 新的在前，含泄漏测试
    std::optional<ojson> get(const std::string& id) const;
    bool set_note(const std::string& id, const std::string& note);
    // 运行的时间段与通道（给 /runs/{id} 的趋势、导出用）
    struct Range {
        int64_t start = 0, end = 0;
        std::vector<int> channels;
        bool leak = false;
    };
    std::optional<Range> range(const std::string& id, int64_t now_ms) const;

private:
    struct ChTrack {
        std::string started_at;    // program_runs[i].startedAt（区分同一路的不同次运行）
        bool open = true;
        std::map<int, int64_t> step_start;
        double max_rate = 0;
    };
    struct Rec {
        ojson j;                                    // 对外的形状（不含 trend / program / stepMinutes 之外的内部字段）
        int64_t start_ms = 0;
        std::map<int, ChTrack> chs;                 // 通道 → 跟踪状态
    };
    void save(const Rec& r) const;
    void finish_channel(Rec& r, int ch, const ojson& run, const ctl::ChannelSnap& c, const Limits& lim, int64_t now);
    void finish_run(Rec& r, int64_t now);
    std::vector<ojson> leak_runs() const;

    std::string dir_, data_dir_;
    mutable std::mutex mu_;
    std::map<std::string, Rec> runs_;
    std::map<std::string, std::string> leak_notes_;   // 泄漏测试运行的备注（runs/<runId>.json 只存备注）
};

}  // namespace reactor::api
