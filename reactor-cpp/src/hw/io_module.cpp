#include "hw/io_module.hpp"

namespace reactor::hw {

ModbusRequest IoModule::req_write_valves(const Valves& open) const {
    std::vector<bool> levels(kValveCount);
    for (int i = 0; i < kValveCount; ++i) levels[i] = level(open[i]);
    return req_set_coils(0, std::move(levels));
}

ModbusRequest IoModule::req_close_range(int start, int count) const {
    return req_set_coils(start, std::vector<bool>(static_cast<size_t>(count < 0 ? 0 : count), level(false)));
}

std::optional<std::vector<bool>> IoModule::decode_coils(const ModbusResult& r) const {
    if (!r.ok || r.bits.size() != static_cast<size_t>(kCoils)) return std::nullopt;
    return r.bits;
}

std::optional<IoModule::Valves> IoModule::decode_open_states(const ModbusResult& r) const {
    auto c = decode_coils(r);
    if (!c) return std::nullopt;
    Valves v{};
    for (int i = 0; i < kValveCount; ++i) v[i] = open_from((*c)[i]);
    return v;
}

std::optional<std::vector<bool>> IoModule::decode_inputs(const ModbusResult& r) const {
    if (!r.ok || r.bits.size() != static_cast<size_t>(kCoils)) return std::nullopt;
    return r.bits;
}

}  // namespace reactor::hw
