// 搅拌：传动换算 + 程序共用搅拌的引用计数。
// C#: Models/StirGear.cs、Models/StirShare.cs
#pragma once
#include <optional>
#include <ostream>
#include <vector>

namespace reactor::core {

// 一台电机接 85 齿大齿轮带 8 个釜上 38 齿小齿轮，传动比 = 电机转速 ÷ 釜转速 = 38/85。
namespace stir_gear {
constexpr double kDefaultRatio = 38.0 / 85.0;  // 电机 ÷ 釜（85 齿大轮带 38 齿小轮）
// 釜转速 → 电机转速（舍入到整数，.5 取偶同 C# Math.Round；限幅 0..max_motor；传动比非正按 1）
int to_motor(int vessel_rpm, double ratio, int max_motor);
// 电机转速 → 釜转速（传动比非正按 1）
double to_vessel(double motor_rpm, double ratio);
}  // namespace stir_gear

struct StirHolder {
    int rv = 0;
    int rpm = 0;
    bool operator==(const StirHolder& o) const { return rv == o.rv && rpm == o.rpm; }
};
inline std::ostream& operator<<(std::ostream& os, const StirHolder& h) { return os << "(" << h.rv << "," << h.rpm << ")"; }

// 全机共用一台搅拌器的程序引用计数（只在控制线程用）。持有者按「最近登记」排序（重复登记移到末尾）。
// 操作员手动开停搅拌不经这里。
class StirShare {
public:
    // 登记 rv 的转速请求；返回此前设定全局转速的另一程序（转速不同才有值），即本次改写了谁
    std::optional<StirHolder> request(int rv, int rpm);
    // 释放 rv；返回是否仍有其他持有者（true = 不要停搅拌）。释放的是最近设定者时「最近设定」回退到剩余最近登记者
    bool release(int rv);
    bool holds(int rv) const;
    int count() const { return static_cast<int>(holders_.size()); }
    std::vector<int> holders() const;
    // 最近一次经程序设定的全局转速
    std::optional<int> last_rpm() const { return last_rpm_; }
    // 剩余持有者中最近登记的那一位；释放后全局转速应以它为准
    std::optional<StirHolder> current() const;
    // 全部停止：清空
    void clear();

private:
    std::vector<StirHolder> holders_;
    std::optional<int> last_setter_;
    std::optional<int> last_rpm_;
};

}  // namespace reactor::core
