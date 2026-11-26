// 报警表（/alarms 的数据源）。C#: ViewModels/AlarmViewModel.cs（Log / Raise / Clear / HasActive / AckCore）+ core::AlarmLifecycle
//
//   log(level, rv, title, detail, kind)   追加一条（info → 状态 done；warn/err → active），同时写事件（显式 kind）
//   raise(key, ...)                        同 log，另外按条件键登记（info 不登记）
//   clear(key, rv, title, detail, kind)    条件消失：该键下全部条目 active → cleared、ack → done，再记一条 info「解除」
//   ack(id)                                人工确认：active → ack、cleared → done
//
// 级别：事件级别沿用 C# 的 info / warn / err；另外给出界面的三级（ui_level）：
//   1 = 安全联锁（条件键 ot:N 超温切断、op:N 超压联锁、opv:N 超压关阀未确认、oph:N 超压停热未确认，无论 warn/err）
//   2 = 错误（其他 err：模块离线 comm:k、搅拌故障 stir:fault、启动加热失败、阀门写入失败…）
//   3 = 警告（其他 warn：升温超时 ht:N、压力偏离 pd:N、放热超出控温能力…）
//   0 = 普通事件（info，不算报警）
// 条目 id 是本次运行内递增的十进制字符串（同 C#），重启清空。表长上限 kMaxItems，超出时丢最旧的非活动条目。
//
// 线程：只在控制线程用。事件写入经 sink（写 DataLogger / EventStore），变化经 on_change（给 WS alarm 消息），都在控制线程上回调。
#pragma once
#include <cstdint>
#include <deque>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "core/alarm_lifecycle.hpp"
#include "nlohmann/json.hpp"
#include <utility>

namespace reactor::ctl {

struct AlarmItem {
    std::string id;
    std::string level;    // info / warn / err
    int ui_level = 0;     // 1 安全联锁 / 2 错误 / 3 警告 / 0 普通事件
    std::string rv;       // 来源："RV3" / "通讯" / "系统" / "搅拌"…
    std::optional<int> channel;
    std::string title;
    std::string message;
    int64_t raised_at_ms = 0;
    std::string state;    // active / ack / cleared / done
    std::optional<int64_t> cleared_at_ms;
    std::string key;      // 条件键（没有为空）
    std::string kind;     // 事件 kind
};

// C# AlarmDto 形状（GET /alarms 一项、WS alarm 消息体），末尾追加 uiLevel
nlohmann::ordered_json alarm_to_json(const AlarmItem& a);
int alarm_ui_level(const std::string& key, const std::string& level);

class AlarmTable {
public:
    static constexpr size_t kMaxItems = 5000;
    using EventSink = std::function<void(const std::string& level, const std::string& origin, const std::string& title,
                                         const std::string& detail, const std::string& kind)>;

    AlarmTable(std::function<int64_t()> wall_now, EventSink sink) : now_(std::move(wall_now)), sink_(std::move(sink)) {}

    const AlarmItem& log(const std::string& level, const std::string& rv, const std::string& title, const std::string& detail,
                         const std::string& kind);
    const AlarmItem& raise(const std::string& key, const std::string& level, const std::string& rv, const std::string& title,
                           const std::string& detail, const std::string& kind);
    int clear(const std::string& key, const std::string& rv, const std::string& title, const std::string& detail,
              const std::string& kind);
    bool has_active(const std::string& key) const;
    // 人工确认；找不到返回 false
    bool ack(const std::string& id);
    // 全部确认（新端点 POST /alarms/ack-all），返回改动条数
    int ack_all();

    const std::deque<AlarmItem>& items() const { return items_; }  // 新的在前
    const AlarmItem* find(const std::string& id) const;
    // filter：active / ack / cleared / done / all（同 C# /alarms?state=）
    std::vector<AlarmItem> list(const std::string& state_filter = "all") const;
    int active_count() const;

    std::function<void(const AlarmItem& changed)> on_change;

private:
    const AlarmItem& add(const std::string& key, const std::string& level, const std::string& rv, const std::string& title,
                         const std::string& detail, const std::string& kind);
    AlarmItem* find_mut(const std::string& id);
    void trim();

    std::function<int64_t()> now_;
    EventSink sink_;
    std::deque<AlarmItem> items_;
    std::vector<std::pair<std::string, std::string>> index_;  // (key, id)：未解除的登记（AlarmIndex 的等价物，存 id）
    int64_t seq_ = 0;
};

}  // namespace reactor::ctl
