// 界面用的整份数据：遥测快照 + 设置 + 报警 / 事件 + 现有接口里还没有、触屏新界面要用的几项（*Ext）。
// net/ 收到数据后拼成一份 Model，经 ModelMailbox 交给 LVGL 线程；页面只读 Model。
#pragma once
#include "ext2.hpp"
#include "telemetry.hpp"

#include <memory>
#include <mutex>

namespace reactor::hmi::model {

// ---- 现有 /api/v1 里没有的（设计文档 §5.4 的 WS progress 等，需要后端补）----

// 程序进度：方案总览、点釜面板、全部设定要用
struct ProgressExt {
    std::string programName;               // 「加氢筛选-A」
    std::vector<std::string> stepNames;    // 每步的中文短名（气密检查 / 置换 / 升温 …）
    std::string stepTarget;                // 当前步的目标（「80 °C」「2.0」）
    double stepFraction = 0;               // 当前步完成比例 0..1（不知道就 0）
    int64_t elapsedSec = 0;                // 程序已运行
    std::optional<int64_t> remainingSec;   // 约剩（含「最长」的反应步按最长估）
    std::optional<int> gasQueueRank;       // 排队等气路时排第几（1 起）
    std::vector<int> waitingFor;           // 在等哪几路放开气路
    int64_t waitedSec = 0;
    bool editedNotSent = false;            // 已编辑、还没下发
    int stepCountDraft = 0;                // 未下发时的步数
    int64_t draftTotalSec = 0;             // 未下发时的预计总时长
};

// 方案（甘特图）里的一段
struct PlanSeg {
    std::string kind;        // leak / purge / heat / stir / press / hold / react / vent / end
    std::string name;        // 显示名
    int64_t start = 0, end = 0;  // UTC 毫秒；反应步 end = 最长时间
    bool openEnded = false;  // 反应步：时长看吸气，可能提前结束
    int64_t earliestEnd = 0;
};
struct PlanPoint { int64_t t; double v; };
struct PlanExt {
    std::vector<PlanSeg> segs;
    std::vector<PlanPoint> tempPlan;   // 计划温度 °C（台阶 / 斜坡的拐点）
    std::vector<PlanPoint> pressPlan;  // 计划压力 MPa
    std::vector<PlanPoint> tempActual, pressActual;  // 已走过的实测（方案总览点开一路时画）
};

// 报警的界面信息：分级和「系统已经做了 / 接下来」。现有 AlarmDto 只有 level/title/message。
struct AlarmExt {
    int tier = 3;                       // 1 = 安全联锁（全屏）/ 2 = 错误（弹窗）/ 3 = 警告（铃铛气泡）/ 0 = 事件
    std::string source = "system";      // channel / comm / system
    std::string quantity;               // pressure / temp / ""
    std::optional<double> value, setpoint, limit, rangeLo, rangeHi;  // 显示用（压力已换成 MPa）
    std::vector<std::string> did, next; // 系统已经做了 / 接下来
    std::string context;                // 「程序「加氢筛选-B」第 6 步 反应」
    int64_t downSince = 0;              // 二级通讯类：从什么时候断开
    std::string hint;                   // 二级：处理建议
    std::string highlight;              // 二级：浅红底那一行
};
struct AlarmItem { AlarmDto dto; AlarmExt ext; };

// 单路的手动设定里，快照没有直接给出的几项（到位范围、防超调开关、曲线各段、加热开关）。
// 曲线各段在 /channels/{id} 的 profile 里有；到位范围、防超调要后端在 /channels/{id} 里补。
struct CurveSeg { double tempC; double minutes; };
struct ChanSetExt { double band = 2; bool antiOvershoot = true; bool heatOn = false; std::vector<CurveSeg> curve; };

// PC 申请远程控制、等触屏允许 / 拒绝（WS control 消息的 pending、GET /control 的 pending）
struct ControlPending {
    std::string requestId, clientName, tokenName;
    int64_t expiresAt = 0;   // UTC 毫秒；0 = 不知道
};

struct Model {
    bool connected = true;
    std::optional<ControlPending> controlPending;  // 有待处理的控制申请 → 弹「允许 / 拒绝」
    std::string linkStatus;             // 断线时盖层下面的小字（连的哪里、第几次、为什么连不上），net/ 填
    TelemetrySnapshot snap;
    SettingsDto settings;
    std::array<ProgressExt, 8> progress;
    std::array<PlanExt, 8> plans;
    std::array<ChanSetExt, 8> chanset;
    std::vector<AlarmItem> alarms;      // 新的在前
    bool alarmsLoaded = true;           // 报警表已经整份取到过（net/ 连上后第一次 GET /alarms 之前为 false）
    std::vector<EventRecord> events;    // 新的在前
    int64_t bootAt = 0;
    std::string languageCode = "zh";
    PagesExt x;                         // 第二批页面的数据（ext2.hpp）

    const ChannelSnap& ch(int id) const { return snap.channels.at(static_cast<size_t>(id - 1)); }
    int unacked() const {
        int n = 0;
        for (auto& a : alarms) n += a.dto.state == "active";
        return n;
    }
};

// 网络线程 → LVGL 线程：只交换最新一份（旧的没取走就被覆盖）。任何线程都可以 post；take 只在 LVGL 线程调。
class ModelMailbox {
public:
    void post(std::shared_ptr<const Model> m) {
        std::lock_guard<std::mutex> lk(mu_);
        latest_ = std::move(m);
    }
    std::shared_ptr<const Model> take() {
        std::lock_guard<std::mutex> lk(mu_);
        return std::move(latest_);
    }

private:
    std::mutex mu_;
    std::shared_ptr<const Model> latest_;
};

}  // namespace reactor::hmi::model
