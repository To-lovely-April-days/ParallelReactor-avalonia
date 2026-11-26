#include "core/program/program_run_info.hpp"
#include "core/chan_name.hpp"
#include <string>
#include <vector>

namespace reactor::core::program {

bool ProgramRunInfo::is_active() const { return program_run_set::is_active(phase); }

namespace program_run_set {

bool is_active(ProgramRunPhase p) {
    return p == ProgramRunPhase::Preparing || p == ProgramRunPhase::Running || p == ProgramRunPhase::WaitingGas ||
           p == ProgramRunPhase::Finishing;
}

bool is_owned(const Runs& runs, int rv) {
    auto it = runs.find(rv);
    return it != runs.end() && it->second.is_active();
}

std::vector<int> running_ids(const Runs& runs) {
    std::vector<int> ids;
    for (const auto& kv : runs)   // std::map 已按通道号升序
        if (kv.second.is_active()) ids.push_back(kv.second.rv);
    return ids;
}

const ProgramRunInfo* first_running(const Runs& runs) {
    for (const auto& kv : runs)
        if (kv.second.is_active()) return &kv.second;
    return nullptr;
}

std::string summary(const Runs& runs) {
    auto ids = running_ids(runs);
    if (ids.empty()) return "";
    std::string s = "正在运行：";
    return s + chan_names(ids);
}

const char* phase_name(ProgramRunPhase p) {
    switch (p) {
        case ProgramRunPhase::Preparing: return "preparing";
        case ProgramRunPhase::Running: return "running";
        case ProgramRunPhase::WaitingGas: return "waitingGas";
        case ProgramRunPhase::Finishing: return "finishing";
        case ProgramRunPhase::Completed: return "completed";
        case ProgramRunPhase::Failed: return "failed";
        case ProgramRunPhase::Aborted: return "aborted";
    }
    return "";
}

}  // namespace program_run_set
}  // namespace reactor::core::program
