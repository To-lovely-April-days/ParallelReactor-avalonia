// 给人看的通道名：「3 号」「2、5 号」。产品规定新界面里不出现「RV」（联调 2026-11-26）。
// 只用于文案（事件标题 / 说明、报警、提示、程序消息）。报警来源字段（origin = "RV3"，store::event_kinds::channel_of 靠它
// 认通道）、遥测 CSV 列名（RVx_…）、泄漏记录 CSV 的 RV 列保持原样（文件格式兼容 C#）。
#pragma once
#include <string>
#include <vector>

namespace reactor::core {

inline std::string chan_name(int id) { return std::to_string(id) + " 号"; }

// 「2、5 号」；sep 是号码之间的分隔
inline std::string chan_names(const std::vector<int>& ids, const char* sep = "、") {
    std::string s;
    for (size_t i = 0; i < ids.size(); ++i) {
        if (i) s += sep;
        s += std::to_string(ids[i]);
    }
    return ids.empty() ? s : s + " 号";
}

}  // namespace reactor::core
