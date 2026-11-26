// 宇电 AI-8 多回路温控器（一台一实例，1 拖 8）。C#: Hardware/TempController.cs + Service/TempService.cs 里的寄存器操作
//
// 功能码 0x03 读 / 0x06 写单。寄存器（回路 loop 从 1 起）：
//   SP  0x0000+(loop-1)  给定值（有符号，÷10^dPt）
//   P   0x0060+(loop-1)  比例带（有符号，÷10^dPt，单位同 SP）
//   I   0x00C0+(loop-1)  积分时间（无符号，0.1 s）
//   D   0x0120+(loop-1)  微分时间（有符号，0.01 s）
//   At  0x0300+(loop-1)  工作模式：0 APID / 1 自整定 / 2 ON-OFF / 3 手动 / 4 停止（5 PV 变送）
//   OP  0x0360+(loop-1)  输出值（25600 = 100%）
//   SV  0x0480+(loop-1)  实际给定值（升降温斜率时为过程给定；有符号 ÷10^dPt）
//   PV  0x0600+(loop-1)  测量值（只读，有符号，÷10^dPt）
//   Ctn 0x0844           启用回路数（<8 时后面的通道不受控）
//   Srun 0x0845          9655 全局停止 / 0 全运行 / 15 运行但断电重启后自动全停
//
// 用法有两种，换算和寄存器只写一处：
//   1) 异步（控制线程用）：req_*() 组请求 → Bus::submit / submit_seq → 回调里 decode_*() 解码；
//   2) 同步（工具 / 测试 / 慢流程）：read_* / set_* 直接在某个 ModbusMaster 上做（经 BusMaster 时会阻塞）。
// 对象本身只存站号 / 回路数 / dPt，不可变，任何线程可用。
#pragma once
#include <cstdint>
#include <optional>
#include <vector>

#include "hw/modbus_master.hpp"

namespace reactor::hw {

struct PidParams {
    double p = 0;  // 工程量（同 SP 单位）
    double i = 0;  // 秒
    double d = 0;  // 秒
};

// 一轮轮询的结果（C# TempService.PollAsync：PV、OP、At 各块读 8 路 + Srun + Ctn，共 5 次读）
struct TempPoll {
    std::vector<int16_t> pv_raw;
    std::vector<double> pv;      // ℃
    std::vector<double> op_pct;  // 0..100
    std::vector<int> at;         // 工作模式
    int srun = -1;
    int ctn = -1;
};

class TempController {
public:
    static constexpr uint16_t kSpBase = 0x0000;
    static constexpr uint16_t kPBase = 0x0060;
    static constexpr uint16_t kIBase = 0x00C0;
    static constexpr uint16_t kDBase = 0x0120;
    static constexpr uint16_t kAtBase = 0x0300;
    static constexpr uint16_t kOpBase = 0x0360;
    static constexpr uint16_t kSvBase = 0x0480;
    static constexpr uint16_t kPvBase = 0x0600;
    static constexpr uint16_t kCtnReg = 0x0844;
    static constexpr uint16_t kSrunReg = 0x0845;

    static constexpr int kAtApid = 0;
    static constexpr int kAtAutotune = 1;
    static constexpr int kAtOnOff = 2;
    static constexpr int kAtManual = 3;
    static constexpr int kAtStop = 4;

    static constexpr int kSrunRun = 0;
    static constexpr int kSrunAutoStopAfterPowerLoss = 15;
    static constexpr int kSrunStopAll = 9655;

    static constexpr double kOutputFull = 256.0;  // OP 原码 ÷ 256 = %（25600 = 100%）

    // loops：C# 构造默认 4，但 TempService 一律按 8 建（1 拖 8），这里默认 8。dpt：同仪表 dPt。
    explicit TempController(uint8_t slave = 1, int loops = 8, int dpt = 1);

    uint8_t slave() const { return slave_; }
    int loops() const { return loops_; }
    int dpt() const { return dpt_; }
    double scale() const { return scale_; }

    // ---- 换算 ----
    double from_raw(int16_t raw) const { return raw / scale_; }  // C#: PvFromRaw
    uint16_t temp_to_reg(double c) const { return reg_from_i16(c * scale_); }  // SP / SV：(ushort)(short)Math.Round(c×10^dPt)

    // ---- 请求 ----
    ModbusRequest req_read_pv_block() const;
    ModbusRequest req_read_op_block() const;
    ModbusRequest req_read_at_block() const;
    ModbusRequest req_read_sv_block() const;
    ModbusRequest req_read_sp_block() const;
    ModbusRequest req_read_pv(int loop) const;
    ModbusRequest req_read_sp(int loop) const;
    ModbusRequest req_read_sv(int loop) const;
    ModbusRequest req_read_op(int loop) const;
    ModbusRequest req_read_at(int loop) const;
    ModbusRequest req_read_srun() const;
    ModbusRequest req_read_ctn() const;
    ModbusRequest req_write_ctn(int loops) const;
    ModbusRequest req_write_srun(int v) const;
    ModbusRequest req_stop_all() const { return req_write_srun(kSrunStopAll); }  // Srun=9655
    ModbusRequest req_run_all() const { return req_write_srun(kSrunRun); }       // Srun=0
    ModbusRequest req_write_sp(int loop, double c) const;
    ModbusRequest req_write_at(int loop, int mode) const;
    ModbusRequest req_start(int loop) const { return req_write_at(loop, kAtApid); }
    ModbusRequest req_stop(int loop) const { return req_write_at(loop, kAtStop); }
    ModbusRequest req_autotune(int loop) const { return req_write_at(loop, kAtAutotune); }
    ModbusRequest req_read_p(int loop) const;
    ModbusRequest req_read_i(int loop) const;
    ModbusRequest req_read_d(int loop) const;
    ModbusRequest req_write_p(int loop, double p) const;        // (ushort)Math.Round(p×10^dPt)
    ModbusRequest req_write_i(int loop, double seconds) const;  // (ushort)Math.Round(s×10)
    ModbusRequest req_write_d(int loop, double seconds) const;  // (ushort)(short)Math.Round(s×100)
    std::vector<ModbusRequest> req_write_pid(int loop, double p, double i, double d) const;  // P、I、D 三笔
    std::vector<ModbusRequest> req_read_pid(int loop) const;                                // P、I、D 三笔
    // 一轮轮询的 5 笔块读：PV×n、OP×n、At×n、Srun、Ctn（C# TempService.PollAsync 的顺序）
    std::vector<ModbusRequest> poll_requests() const;
    // 「全部停止」：逐路 At=4（不用 Srun=9655 全局闸，C# StopAllChannelsAsync；设计文档 §4）
    std::vector<ModbusRequest> stop_all_loops_requests() const;

    // ---- 解码（r.ok 且寄存器个数对才有值） ----
    std::optional<std::vector<int16_t>> decode_raw_block(const ModbusResult& r) const;
    std::optional<std::vector<double>> decode_temp_block(const ModbusResult& r) const;   // PV / SV / SP 块 → ℃
    std::optional<std::vector<double>> decode_output_block(const ModbusResult& r) const; // OP 块 → %
    std::optional<std::vector<int>> decode_mode_block(const ModbusResult& r) const;      // At 块
    std::optional<double> decode_temp(const ModbusResult& r) const;                      // 单个 SP/PV/SV/P → 工程量
    std::optional<double> decode_output(const ModbusResult& r) const;
    static std::optional<double> decode_i(const ModbusResult& r);  // 秒
    static std::optional<double> decode_d(const ModbusResult& r);  // 秒（有符号）
    static std::optional<int> decode_u16(const ModbusResult& r);   // At / Srun / Ctn
    std::optional<TempPoll> decode_poll(const std::vector<ModbusResult>& rs) const;   // 5 笔全成功才有值
    std::optional<PidParams> decode_pid(const std::vector<ModbusResult>& rs) const;   // 3 笔全成功才有值

    // ---- 同步便捷（失败返回空 / false） ----
    std::optional<std::vector<double>> read_all_pv(ModbusMaster& m) const;
    std::optional<std::vector<int16_t>> read_all_pv_raw(ModbusMaster& m) const;
    std::optional<std::vector<double>> read_all_outputs(ModbusMaster& m) const;
    std::optional<std::vector<int>> read_all_modes(ModbusMaster& m) const;
    std::optional<std::vector<double>> read_all_sv(ModbusMaster& m) const;
    std::optional<TempPoll> poll(ModbusMaster& m) const;  // 遇到失败即停（同 C# 一轮里任一笔失败整轮算失败）
    std::optional<double> read_pv(ModbusMaster& m, int loop) const;
    std::optional<double> read_setpoint(ModbusMaster& m, int loop) const;
    std::optional<double> read_sv(ModbusMaster& m, int loop) const;
    std::optional<double> read_output(ModbusMaster& m, int loop) const;
    std::optional<int> read_mode(ModbusMaster& m, int loop) const;
    std::optional<int> read_srun(ModbusMaster& m) const;
    std::optional<int> read_ctn(ModbusMaster& m) const;
    std::optional<PidParams> read_pid(ModbusMaster& m, int loop) const;
    bool set_setpoint(ModbusMaster& m, int loop, double c) const;
    bool set_ctn(ModbusMaster& m, int loops) const;
    bool start(ModbusMaster& m, int loop) const;     // At=0
    bool stop(ModbusMaster& m, int loop) const;      // At=4
    bool autotune(ModbusMaster& m, int loop) const;  // At=1
    bool stop_all(ModbusMaster& m) const;            // Srun=9655
    bool run_all(ModbusMaster& m) const;             // Srun=0
    bool set_pid(ModbusMaster& m, int loop, double p, double i, double d) const;  // 依次写 P、I、D，任一失败即停

private:
    uint16_t at_loop(uint16_t base, int loop) const { return static_cast<uint16_t>(base + loop - 1); }
    ModbusRequest block(uint16_t base) const;

    uint8_t slave_;
    int loops_;
    int dpt_;
    double scale_;
};

}  // namespace reactor::hw
