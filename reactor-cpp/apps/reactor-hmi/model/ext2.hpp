// 第二批页面（程序 / 泄漏测试 / 曲线 / 记录 / 设置）要用的数据。
// 现有 /api/v1 里有的照抄 C# 的形状（字段名 = JSON 名）；没有的（*Ext）写明由哪个新端点提供，见模块报告第二批 §5。
#pragma once
#include "program.hpp"
#include "telemetry.hpp"

#include <array>
#include <memory>
#include <string>
#include <vector>
#include <cstdint>
#include <optional>

namespace reactor::hmi::model {

// ============================================================ 设置（GET /settings 的其余部分，照 C# SettingsDto）
struct LeakTestSettingsDto { double testPsi = 50, settleMin = 10, measureMin = 30, rateLimitPsiPerHr = 2, reminderDays = 7; OptT lastTestUtc; };
struct ApproachSettingsDto { double deltaC = 5, rateCPerMin = 1, fastRateCPerMin = 1, finalZoneC = 1, settleBandC = 0.2, settleSec = 90; };
struct StirSettingsDto { double peakCurrentA = 1.0, standbyPct = 50, rampMsPer1000Rpm = 1000, microstep = 16, stirGearRatio = 85.0 / 38.0; };
struct VolumeSettingsDto {
    double vesselVolume = 10, gasLineVolume = 1.2;
    OptD manifoldVolume;
    std::array<OptD, 8> effectiveVolume{};
    std::array<OptT, 8> volumeCalibratedAt{};
};
// 触屏新加的设置项（PATCH /settings 新键；PC 不用）
struct UiPrefsExt {
    std::string pressureUnit = "MPa";  // MPa / bar / psi（内部仍是 psi）
    std::string tempUnit = "C";        // C / F
    std::string theme = "light";       // light / dark
    bool preflightChecklist = true;    // 开始前弹出检查清单
    std::string keyboard = "num";      // num / full（点数值框弹哪种键盘）
    double gasWaitMaxMin = 60;         // 等待气路上限（几路程序抢气路时最多等多久）
    double retentionDays = 90;         // 运行记录保留天数
};

// ============================================================ 程序库（GET /library/programs、/library/templates）
struct LibEntry {
    ProgramDoc prog;               // 模板的 note 放在 prog.note
    std::vector<int> usedBy;       // 正在用它的通道（后端按 channels[i].program 的来源 id 算；没有就空）
};

// ============================================================ 趋势数据（曲线页、运行中整次曲线）
// 后端建议：GET /trend?from&to&stepMs → 每路 temp / pressurePsi / gasConsumed 的等间隔数组（取自遥测 CSV + 内存环）。
// 界面拿到后换算成 MPa 存这里；缺数据用 NaN。
struct Trend {
    int64_t t0 = 0;        // 第一个点的时刻（UTC 毫秒）
    int64_t stepMs = 10000;
    std::array<std::vector<float>, 8> temp, press, gas;  // °C、MPa、mmol
    int size() const { return (int)temp[0].size(); }
    int64_t t_at(int i) const { return t0 + stepMs * i; }
    int64_t t_end() const { return t0 + stepMs * (size() > 0 ? size() - 1 : 0); }
};

// ============================================================ 运行记录（新端点 GET /runs；由 SessionManifest.programRuns + 事件 + CSV 汇总）
struct RunChannelStat { int ch = 0; double mmol = 0, maxRate = 0, tempC = 0; bool flagged = false; };
struct RunAbortInfo {
    int ch = 0;
    std::string quantity;              // pressure / temp / manual
    double value = 0, setpoint = 0, limit = 0, rangeHi = 0;   // MPa 或 °C
    int64_t at = 0;
    std::string title;                 // 「超压，程序自动停止」
    std::string did;                   // 「已停止加热、关闭进气阀、停搅拌 · 11:31:40 操作员确认」
};
struct RunRecord {
    std::string id;                    // 运行 id（会话 id + 通道组）
    std::string programName;
    std::string result;                // running / done / warn / abort / leak
    std::string resultText;            // 列表右边那一列：「4.20 mmol」「7 号超压」「8/8 通过」「完成」
    int64_t start = 0, end = 0, plannedSec = 0;
    std::vector<int> channels;
    std::string endReason;             // 「各路都在吸气停止后自动结束」
    double avgMmol = 0, rsdPct = 0, tempDevC = 0, pressMPa = 0;
    int64_t gasStart = 0;              // 开始充反应气的时刻（曲线上的虚线）
    std::vector<RunChannelStat> perCh;
    std::optional<RunAbortInfo> abort;
    std::string note;
    std::shared_ptr<const Trend> trend;  // 这次运行的各路数据（只取 gas 画「各路吸气量」）
    ProgramDoc program;                  // 下发时的副本（「存进程序库」「用同样设置再跑一次」）
    std::vector<int> stepMinutes;        // 每步实际用时（跑完页「用了 36 min」）
};

// ============================================================ 每日数据文件（GET /files?type=telemetry|events，C# HistoryFileInfo）
struct HistoryFileInfo {
    std::string name, type;            // type：telemetry / events
    int64_t bytes = 0, modifiedAt = 0;
    OptT from, to;
    std::string day;                   // yyyyMMdd
    std::string desc;                  // 界面说明（「每 1.2 s 一行 …」「15:42 改了压力单位 …」，后端给 reason）
    int lines = 0;                     // events：条数
};
struct DayRunRef { std::string runId, name, result; int64_t start = 0, end = 0; std::vector<int> channels; };
struct DayInfo {
    std::string day;                   // yyyyMMdd
    int64_t bytes = 0;
    std::vector<HistoryFileInfo> files;
    std::vector<DayRunRef> runs;
};

// ============================================================ U 盘（GET /usb）
struct UsbExt { bool present = false; std::string label; int64_t freeBytes = 0; std::string lastExport; };

// ============================================================ 泄漏测试的逐点数据和历史
// 逐点：WS leak 或 GET /leak/current/samples（LeakRecord 的 samples.csv）：测量阶段起每 10 s 一点，Δp 相对测量开始（kPa）
struct LeakSample { double min = 0; std::array<float, 8> dpKPa{}; };
struct LeakHistoryItem { int64_t at = 0; std::string channels; bool pass = true; std::string note; };
struct LeakExt {
    std::vector<int> selectable;       // 现在能测的（空闲）通道
    std::vector<LeakSample> samples;
    std::vector<LeakHistoryItem> history;   // GET /leak-tests 汇总（新的在前）
};

// ============================================================ 温控 PID（GET /pid，C# PidDto）
struct PidBandSnap { int index = 0; std::string name; double loC = 0, hiC = 0; };
struct PidBandValueSnap { int index = 0; double P = 0, I = 0, D = 0; OptD tunedSpC; OptT tunedAt, manualEditedAt; };
struct PidChannelSnap {
    int id = 0; bool autotuning = false; OptI tuneBand; OptD tuneSpC;
    std::vector<PidBandValueSnap> bands;
    // 触屏新加（WS progress / GET /pid）：自整定进度
    int tuneCycle = 0, tuneCycles = 3; OptD tuneLeftSec; double tuneFraction = 0;
};
struct PidExt { std::vector<PidBandSnap> bands; std::array<PidChannelSnap, 8> channels; std::string controller = "宇电 AI-8"; };

// ============================================================ 容积标定（遥测 calib + GET /calib 的逐次读数）
struct CalibReading { int n = 0; double manifoldFrom = 0, manifoldTo = 0; OptD vesselFrom, vesselTo; };  // MPa
struct CalibExt {
    std::vector<CalibReading> readings;
    int pass = 1, passes = 2;          // 第几遍 / 共几遍（第一次标定要加水做两遍）
    int cycle = 0, cycles = 6;         // 这一遍第几次充放
    std::array<int, 4> stepMinutes{};  // 各阶段用时（显示「12 min」）
    double vesselTempC = 25;
    std::string gas = "惰性气体";
};

// ============================================================ 通道与气路（GET /device + hardware.json 的接线）
struct IoMapExt {
    std::array<int, 3> inletCoil{8, 9, 10};  // 惰性 / A / B
    int ventCoil = 11;
    std::array<int, 8> svCoil{0, 1, 2, 3, 4, 5, 6, 7};
    std::array<int, 8> aiChannel{1, 2, 3, 4, 5, 6, 7, 8};
    std::string ioModel = "IO16R", aiModel = "8AI";
};

// ============================================================ 通讯与模块（GET /device/comm，C# CommDto / ModuleCommSnap + HardwareDto）
struct ModuleExt {
    std::string id;                    // temp / pressure / io / stir
    std::string name, model, detail;   // 「温控」「宇电 AI-8」「8 路 PID · 1 拖 8」
    bool configured = true, online = true;
    std::string port; int baud = 9600;
    int64_t polls = 0, fails = 0;
    OptT lastOkAt, offlineSince;
    int failStreak = 0;
};
struct CommExt { std::vector<ModuleExt> modules; double pollSec = 1.2; int offlineAfter = 3; };

// ============================================================ 网络（GET /network）
struct WifiNet { std::string ssid; bool secured = true; int strength = 3; /*1..4*/ bool saved = false; };
struct NetworkExt {
    bool wifiOn = true;
    std::string connectedSsid, ip, gateway = "192.168.1.1", hostname = "htlab-pr8";
    int strength = 4; bool autoConnect = true;
    std::vector<WifiNet> nearby;
    bool ethernetPlugged = false; std::string ethernetIp;
    bool dhcp = true;
    bool scanning = false;
};

// ============================================================ 数据存储（GET /storage）
struct StorageExt {
    int64_t diskTotal = 0, diskFree = 0, systemBytes = 0, dataBytes = 0;
    std::string settingsPath, dataDir;
    int64_t todayTelemetryBytes = 0, todayEventsBytes = 0; int leakRuns = 0;
};

// ============================================================ 关于 / 用户（GET /device、GET /users）
struct AboutExt { std::string model = "HT-PR8", version = "0.8.3", serial; };
struct UsersExt { std::string mode = "single"; std::string role = "admin"; };

// 第二批的全部数据（Model::x）
struct PagesExt {
    ProgramSchema schema;                         // GET /program-schema（设置 revision 变了重新取）
    std::array<ProgramDoc, 8> programs;           // GET /channels/{id}/program：每一路当前的程序（运行中的是下发时的副本）
    std::array<std::string, 8> programSrcId;      // 来自程序库哪一条（usedBy 用）
    std::vector<LibEntry> library, templates;
    int libraryMax = 50;
    std::shared_ptr<const Trend> trend;           // 最近一段（曲线页 / 运行中）
    std::vector<RunRecord> runs;                  // 新的在前
    std::vector<DayInfo> days;                    // 有数据的日子（新的在前）
    int retentionDaysShown = 90;
    UsbExt usb;
    LeakExt leak;
    LeakTestSettingsDto leakSettings;
    ApproachSettingsDto approach;
    StirSettingsDto stir;
    VolumeSettingsDto volume;
    UiPrefsExt prefs;
    PidExt pid;
    CalibExt calib;
    IoMapExt io;
    CommExt comm;
    NetworkExt net;
    StorageExt storage;
    AboutExt about;
    UsersExt users;
};

}  // namespace reactor::hmi::model
