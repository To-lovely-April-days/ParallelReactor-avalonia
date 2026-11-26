// 压力 PID 整定参数。C#: Models/PidParams.cs（HTML 里的 pid:{maxpulse,pboost}，默认值照抄）
#pragma once

namespace reactor::core {

struct PidParams {
    double max_pulse = 3;  // C#: _maxPulse = 3
    double p_boost = 7;    // C#: _pBoost = 7
};

}  // namespace reactor::core
