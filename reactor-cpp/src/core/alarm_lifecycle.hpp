// 报警条目的状态流转 + 按条件键登记未解除条目。C#: Models/AlarmLifecycle.cs
//  active —条件消失→ cleared；active —确认→ ack；ack —条件消失→ done；cleared —确认→ done；done 不再变化。
// 报警清除不等于通道状态复位：通道仍保持「报警」，需要操作员停止 / 复位。
#pragma once
#include <map>
#include <string>
#include <vector>

namespace reactor::core {

namespace alarm_lifecycle {
inline constexpr const char* kActive = "active";
inline constexpr const char* kAck = "ack";
inline constexpr const char* kCleared = "cleared";
inline constexpr const char* kDone = "done";

// 报警条件消失（超温回落 / 超压回落 / 通讯恢复）
std::string on_cleared(const std::string& stat);
// 操作员确认
std::string on_ack(const std::string& stat);
}  // namespace alarm_lifecycle

// 按条件键（"ot:3" 超温、"op:3" 超压、"comm:0" 掉线）登记未解除的报警条目，条件消失时一次取出该键下全部条目。
template <class T>
class AlarmIndex {
public:
    void raise(const std::string& key, T item) { map_[key].push_back(std::move(item)); }
    int count(const std::string& key) const {
        auto it = map_.find(key);
        return it == map_.end() ? 0 : static_cast<int>(it->second.size());
    }
    // 取出并移除该键下的全部条目（没有时返回空表）
    std::vector<T> take_active(const std::string& key) {
        auto it = map_.find(key);
        if (it == map_.end()) return {};
        std::vector<T> out = std::move(it->second);
        map_.erase(it);
        return out;
    }

private:
    std::map<std::string, std::vector<T>> map_;
};

}  // namespace reactor::core
