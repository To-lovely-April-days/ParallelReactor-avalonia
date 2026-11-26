#include "derive.hpp"

#include "../ui/kit/i18n.hpp"
#include "../ui/kit/theme.hpp"

#include <limits>
#include <cmath>
#include <cstdio>
#include <ctime>

namespace reactor::hmi::model {

VState vstate(const Model& m, int id) {
    const ChannelSnap& c = m.ch(id);
    if (!c.installed || c.state == "idle") return VState::Disabled;
    if (!c.tempOnline || !c.pressurePsi) return VState::Offline;
    if (c.state == "alarm" || c.overTempTripped || c.overPressureTripped) {
        if (c.alarmCause && *c.alarmCause == "overPressure") return VState::OverPressure;
        if (c.overPressureTripped) return VState::OverPressure;
        return VState::OverTemp;
    }
    if (c.program && c.program->waitingGas) return VState::QueuedGas;
    if (c.state == "heating") return VState::Heating;
    if (c.state == "pressing") return VState::Pressing;
    if (c.state == "react") return VState::Reacting;
    if (c.state == "wait") return VState::Holding;
    if (c.state == "stir") return VState::Stirring;
    if (c.state == "cooling") return VState::Cooling;
    if (c.state == "done") return VState::Done;
    return VState::Ready;
}

uint32_t badge_color(VState s) {
    switch (s) {
        case VState::Disabled: return col::ST_OFF;
        case VState::Ready: return col::ST_BLU;
        case VState::Heating: return col::ST_ORG;
        case VState::Pressing:
        case VState::Reacting: return col::ST_GRN;
        case VState::Holding: return col::ST_TEAL;
        case VState::Stirring: return col::ST_BLU;
        case VState::Cooling: return col::ST_CYN;
        case VState::QueuedGas: return col::ST_AMB;
        case VState::Done: return col::ST_GRY;
        case VState::OverTemp:
        case VState::OverPressure: return col::ST_RED;
        case VState::Offline: return col::ST_AMB;
    }
    return col::ST_BLU;
}

bool vs_running(VState s) {
    switch (s) {
        case VState::Heating: case VState::Pressing: case VState::Reacting: case VState::Holding:
        case VState::Stirring: case VState::OverTemp: case VState::OverPressure: return true;
        default: return false;
    }
}
bool vs_bubbles(VState s) { return s == VState::Reacting || s == VState::Pressing; }
bool vs_heater(VState s) { return s == VState::Heating || s == VState::OverTemp; }

static std::string f(const char* fmt, double v) {
    char b[64];
    std::snprintf(b, sizeof b, fmt, v);
    return b;
}

Hint hint(const Model& m, int id) {
    const ChannelSnap& c = m.ch(id);
    const ProgressExt& p = m.progress[static_cast<size_t>(id - 1)];
    Hint h;
    switch (vstate(m, id)) {
        case VState::Heating: h = {"up", f("%.0f °C", c.tempSpC), col::ST_ORG}; break;
        case VState::Pressing: h = {"up", f("%.3f MPa", psi_to_mpa(c.pressureSpPsi)), col::ST_GRN}; break;
        case VState::Reacting:
            h = {"drop", c.gasRate ? f("%+.3f /min", *c.gasRate) : std::string("—"), col::ST_GRN};
            break;
        case VState::Holding: h = {"clock", p.stepTarget, col::ST_TEAL}; break;
        case VState::Stirring: h = {"rot", std::to_string(m.snap.stir.rpmSp) + " rpm", col::ST_BLU}; break;
        case VState::Cooling: h = {"snow", tr("自然降温"), col::ST_CYN}; break;
        case VState::QueuedGas:
            h = {"queue", p.gasQueueRank ? trf("排第 %d", *p.gasQueueRank) : std::string(tr("等气路")), col::ST_AMB};
            break;
        case VState::Done: {
            // 程序结束后后台不再推这一路的进度（elapsedSec 停在 0）：按程序的结局写字，不写「0 min」
            bool ab = c.program && (c.program->state == "aborted" || c.program->state == "failed");
            if (ab) h = {"alert", tr("程序已中止"), col::ST_RED};
            else h = {"check", p.elapsedSec > 0 ? fmt_hmin(p.elapsedSec) : std::string(tr("已结束")), col::ST_GRY};
            break;
        }
        case VState::OverTemp: h = {"alert", trf("限 %.0f °C", m.settings.alarms.overTempC), col::ST_RED}; break;
        case VState::OverPressure:
            h = {"alert", trf("限 %.3f MPa", psi_to_mpa(m.settings.alarms.overPressurePsi)), col::ST_RED};
            break;
        case VState::Offline: h = {"off", tr("读数中断"), col::ST_AMB}; break;
        default: break;
    }
    return h;
}

double temp_sp(const ChannelSnap& c) { return c.tempSpC > 0 ? c.tempSpC : std::numeric_limits<double>::quiet_NaN(); }

std::string fmt_temp(const ChannelSnap& c) { return c.tempC ? f("%.1f", *c.tempC) : std::string("—"); }
std::string fmt_mpa(OptD psi, int d) {
    if (!psi) return "—";
    char fm[8];
    std::snprintf(fm, sizeof fm, "%%.%df", d);
    return f(fm, psi_to_mpa(*psi));
}
std::string fmt_mmol(OptD v) { return v ? f("%.1f", *v) : std::string("—"); }

std::string fmt_hms(int64_t s) {
    if (s < 0) s = 0;
    char b[32];
    std::snprintf(b, sizeof b, "%02lld:%02lld:%02lld", (long long)(s / 3600), (long long)(s / 60 % 60), (long long)(s % 60));
    return b;
}
std::string fmt_hmin(int64_t s) {
    if (s < 0) s = 0;
    char b[32];
    long long h = s / 3600, mi = s / 60 % 60;
    if (h > 0) std::snprintf(b, sizeof b, "%lld h %02lld min", h, mi);
    else std::snprintf(b, sizeof b, "%lld min", mi);
    return b;
}
std::string fmt_clock(int64_t ms, bool sec) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char b[16];
    std::strftime(b, sizeof b, sec ? "%H:%M:%S" : "%H:%M", &tm);
    return b;
}

}  // namespace reactor::hmi::model
