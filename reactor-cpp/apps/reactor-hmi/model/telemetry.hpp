// 遥测快照的纯数据结构：逐个对照 C# 的
//   Contracts/ParallelReactor.Contracts/Api/TelemetrySnapshot.cs
//   Contracts/ParallelReactor.Contracts/Services/EventRecord.cs（AlarmDto / EventRecord）
// 成员名直接用 JSON 里的 camelCase 字段名（和 C# 序列化结果一致），以后 JSON → 结构体逐字段对应，
// 所以这里不按 snake_case 命名。可空字段用 std::optional；时间一律 UNIX 毫秒（UTC）。
// 只放数据，不放任何安全判断（判断都在 reactord 里）。
#pragma once
#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace reactor::hmi::model {

using OptD = std::optional<double>;
using OptI = std::optional<int>;
using OptS = std::optional<std::string>;
using OptT = std::optional<int64_t>;  // UTC 毫秒

// §6.3 mode
struct ModeSnap { std::string control = "local"; OptS holder; };
// §6.3 estop（新后端恒为 false，保留字段兼容 PC）
struct EstopSnap { bool active = false; std::string heat, stir, valves; OptT since; bool latched = false; bool restoredOnBoot = false; };
struct CommSnap { bool temp = true, pressure = true, stir = true, io = true; };
// §6.6 approach：phase = off / settle / creep / hold
struct ApproachSnap { std::string phase = "off"; OptD targetC, currentSpC; std::string statusText; };
struct ProfileSegmentSnap { int index = 0; double tempC = 0; double minutes = 0; bool enabled = true; bool active = false; };
struct ProfileSnap {
    bool running = false; int segCount = 0; std::vector<ProfileSegmentSnap> segments;
    double elapsedMin = 0, totalMinutes = 0; bool complete = false; std::string statusText;
};
// channels[i].program：state = preparing / running / waitingGas / finishing / completed / failed / aborted
struct ChannelProgramSnap {
    bool running = false; int stepIndex = 0; int stepCount = 0; std::string state, message; OptS startedBy; OptT startedAt;
    bool waitingGas = false;
    std::optional<double> stepElapsedMin;   // 当前步实际已进行（新字段；旧后台没有）
    // 暂停（2026-11-29 新字段）：暂停中 state = "paused"、running 仍为 true；加热、进气、程序的搅拌都已停
    bool paused = false;
    OptT pausedAt;
};
// 这一路的程序暂停中
inline bool program_paused(const ChannelProgramSnap* p) { return p && p->running && (p->paused || p->state == "paused"); }
// §6.3 单通道。state：idle / ready / heating / pressing / react / wait / stir / cooling / done / alarm
struct ChannelSnap {
    int id = 0;
    bool installed = true;
    std::string state = "idle";
    OptS alarmCause;  // overTemp / overPressure
    bool overTempTripped = false, overPressureTripped = false;
    OptS startedBy;
    OptD tempC;
    double tempSpC = 25;
    OptD tempSpDeviceC, heatOutputPct;
    bool tempOnline = true;
    bool autotuning = false;
    OptI atMode;
    std::string spMode = "fixed";  // fixed / curve
    ApproachSnap approach;
    std::optional<ProfileSnap> profile;
    OptD pressurePsi;
    OptI pressureRaw;
    double pressureSpPsi = 0;
    OptD gasConsumed;  // mmol
    bool valveOpen = false;
    bool programOwned = false;
    std::optional<ChannelProgramSnap> program;
    OptD gasRate;         // mmol/min
    OptD solutionVolume;  // mL
    // 扩展字段 hasActivity：「全部停止」会把这一路转「手动停止」（程序 / 加热温控 / 气路 / 报警）。旧后台没有 → 界面自己推（derive has_activity）
    std::optional<bool> hasActivity;
};
struct StirSnap {
    bool on = false; int rpmSp = 0; bool online = true; bool locked = false; OptD motorCurrentA, busVoltageV; OptS fault;
    OptI alarmCode; int rampMsPer1000Rpm = 0;
};
struct GasInletSnap { std::string id; bool open = false; };  // id：inert / a / b（显示时只叫「惰性气体 / 气体 A / 气体 B」）
struct GasSnap { std::vector<GasInletSnap> inlets; bool ventOpen = false; bool manualValve = false; OptS task, owner; };
struct ProgramSnap { bool running = false; OptI channel; std::string status; int stepIndex = 0, stepCount = 0; std::string phase, detail; };  // 已废弃，保留
struct ProgramsSnap { int running = 0; std::vector<int> channels; OptI gasHolder; std::vector<int> waitingGas; std::string summary; };
struct LeakChannelSnap { int id = 0; std::string status; OptD pressurePsi, ratePsiH; std::string detail; };
struct LeakSnap {
    bool running = false; double progress = 0; OptS phase, runId; bool done = false, failed = false;
    OptS stage; OptD stageLeftSec, totalLeftSec; OptT startedAt; OptS startedBy; OptD targetPsi, thresholdPsiH;
    std::vector<LeakChannelSnap> channels; std::optional<bool> vented; bool aborted = false; OptS failReason; OptT finishedAt;
};
struct SessionSnap { bool active = false; OptS id, testId, file; };
struct CalibSnap {
    bool running = false; OptI channel; std::string stage; bool waitingOperator = false; OptD waterMl, firstPassMl, resultMl;
    double progress = 0; OptS phaseText; OptT startedAt, waitingSince; OptS startedBy; bool done = false, failed = false; OptS resultText;
};
// §6.3 遥测帧
struct TelemetrySnapshot {
    std::string type = "telemetry";
    int64_t seq = 0;
    int64_t ts = 0;
    bool simulated = false;
    int64_t eventSeqApplied = 0;
    ModeSnap mode;
    EstopSnap estop;
    CommSnap comm;
    std::vector<ChannelSnap> channels;  // 8 项，id 1..8
    StirSnap stir;
    GasSnap gas;
    ProgramSnap program;
    ProgramsSnap programs;
    LeakSnap leakTest;
    SessionSnap session;
    std::optional<CalibSnap> calib;
};

// GET /settings 里界面要用的部分（SettingsSnap 的子集，名字照 SettingsDto）
struct TempBandSnap { int index = 0; double loC = 0, hiC = 0; };
struct LimitsDto { double tempSpMaxC = 200, pressureSpMaxPsi = 507.6, rpmMax = 1000, volumeMaxMl = 8; };
struct AlarmSettingsDto { double overPressurePsi = 377.1, overTempC = 200; bool stopHeatOnOverPressure = true; double heatTimeoutMin = 20, pressDeviationPsi = 8; };
struct SettingsDto { LimitsDto limits; AlarmSettingsDto alarms; double pressFullScaleMPa = 4.0; double stirGearRatio = 38.0 / 85.0; int64_t revision = 0; };

// GET /alarms、WS alarm：level = err / warn / info；state = active / acked / cleared
struct AlarmDto {
    std::string id, level, rv;  // rv：C# 的「RV3」来源列，界面不显示
    OptI channel;
    std::string title, message;
    int64_t raisedAt = 0;
    std::string state = "active";
    OptT clearedAt;
};
// 事件（GET /events、WS event）
struct EventRecord {
    int64_t seq = 0; int seqEpoch = 0; int64_t ts = 0; std::string tsLocal, level, kind; OptI channel;
    std::string source, origin; OptS client, opId; std::string title, message;
};

}  // namespace reactor::hmi::model
