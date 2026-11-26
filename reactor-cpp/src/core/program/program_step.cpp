#include "core/program/program_step.hpp"

#include <cmath>

namespace reactor::core::program {

ProgramStep ProgramStep::create(StepType type) {
    ProgramStep s(type);
    s.p_ = step_specs::defaults(type);
    return s;
}

ProgramStep ProgramStep::from_snapshot(const StepSnapshot& snap) {
    ProgramStep s(snap.type());
    s.p_ = snap.params();
    s.segs_ = snap.segments();
    return s;
}

void ProgramStep::ensure_segments() {
    if (!segs_.empty()) return;
    // C#: ProgramStep.EnsureSegments
    segs_ = {{80, 5, 10}, {130, 3, 20}, {150, 2, 30}, {150, 0, 60}};
}

const StepSpec& ProgramStep::spec() const {
    const StepSpec* s = step_specs::find(type_);
    return s ? *s : step_specs::of(StepType::End);
}

StepSnapshot ProgramStep::to_snapshot(PressureUnit unit) const {
    ProgramStep c = *this;
    if (c.type_ == StepType::Heat && c.str("mode", "fixed") == "curve") c.ensure_segments();
    return StepSnapshot::create(c.type_, c.ordinal_, c.name(), c.detail(unit), c.p_, c.segs_);
}

std::vector<HeatSegment> to_heat_segments(const std::vector<Segment>& segs) {
    std::vector<HeatSegment> out;
    out.reserve(segs.size());
    for (const auto& s : segs) out.push_back(HeatSegment{s.temp, s.rate, s.hold});
    return out;
}

DisplayRange display_range(const FieldDef& f, double lo, double hi, PressureUnit unit) {
    if (f.pressure) {
        double k = std::pow(10.0, pressure_decimals(unit));
        lo = std::ceil(psi_to_unit(lo, unit) * k - 1e-9) / k;
        hi = std::floor(psi_to_unit(hi, unit) * k + 1e-9) / k;
        if (hi < lo) hi = lo;
    }
    return {lo, hi};
}

double display_number(const FieldDef& f, double stored, PressureUnit unit) {
    return f.pressure ? round_even(psi_to_unit(stored, unit), pressure_decimals(unit)) : stored;
}

double stored_from_display(const FieldDef& f, double v, PressureUnit unit) {
    double stored = f.pressure ? round_even(unit_to_psi(v, unit), 3) : v;
    if (f.integer) stored = round_even(stored);
    return stored;
}

}  // namespace reactor::core::program
