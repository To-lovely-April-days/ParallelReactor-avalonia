namespace ParallelReactor.Models;

/// <summary>全局可配置参数与报警阈值（设置页编辑，各处引用）。</summary>
public class AppSettings
{
    // 可设置参数范围（上限）
    public double TMaxSp { get; set; } = 200;     // 温度 SP 上限 °C
    public double PMaxSp { get; set; } = 500;     // 压力 SP 上限 psi
    public double RpmMax { get; set; } = 1000;    // 搅拌转速上限 rpm
    public double VolMax { get; set; } = 50;      // 溶液体积上限 mL

    // 搅拌电机（雷赛 DM2C）电流 —— 写入驱动器 Pr5.00 / Pr5.33 并存 EEPROM
    public double StirCurrent { get; set; } = 1.0;     // 峰值电流 A（DM2C-RS432 硬限 0.3–3.2）
    public double StirStandbyPct { get; set; } = 50;   // 待机电流百分比 %（0–100，降低停转发热）

    // 搅拌起停平缓度：加减速时间，单位 ms/1000rpm（越大起停越缓，避免启动甩液/顿挫"起飞"感）
    public double StirRampMs { get; set; } = 1000;     // 默认 1000 → 300rpm 约 0.3s 平滑升起

    // 细分（指令脉冲数/转，Pr0.00）：越高低速越平顺、越不易共振。200–51200
    public double StirMicrostep { get; set; } = 10000;   // 启动后由 MainViewModel 从驱动器回填

    // 压力变送器满量程（MPa）—— 必须与实际变送器量程一致，否则压力读数按比例偏。运行时可在设置页改
    public double PressFullScaleMPa { get; set; } = 4.137;   // 启动后由 MainViewModel 从内部 psi 满程回填

    // 定值加热防超调逼近（见 HeatApproach）：先升到 目标−Δ 并判稳，再慢推到目标
    public double ApproachDelta { get; set; } = 5;        // 预留温差 Δ ℃（须大于实测最大冲温；0=关闭逼近）
    public double ApproachRate { get; set; } = 0.2;       // 末段慢推速率 ℃/min（离目标 ≤ 末段范围时）
    public double ApproachFastRate { get; set; } = 1.0;   // 快段逼近速率 ℃/min（离目标 > 末段范围时，PV 领先则跟随）
    public double ApproachFinalZone { get; set; } = 1.0;  // 末段范围 ℃（只有最后这段慢推、PV 偏高才暂停）
    public double ApproachSettleBand { get; set; } = 0.2; // 判稳范围 ±℃
    public double ApproachSettleSec { get; set; } = 90;   // 判稳时长 秒

    [System.Text.Json.Serialization.JsonIgnore]
    public ApproachParams ApproachParams => new(ApproachDelta, ApproachRate, ApproachSettleBand, ApproachSettleSec,
        ApproachFastRate, ApproachFinalZone);

    // 报警阈值
    public double OverPressure { get; set; } = 510;   // 超压报警 psi
    public double OverTemp { get; set; } = 250;       // 超温报警 °C
    public bool StopHeatOnOverPressure { get; set; } = true;   // 超压时停止加热（超压联锁同时下发该通道 At=4）
    public double HeatTimeout { get; set; } = 20;     // 升温超时 分钟
    public double PressDeviation { get; set; } = 8;   // 压力偏离 SP 报警 psi
    public double LeakRate { get; set; } = 2;         // 泄漏率阈值 psi/hr
    public double LeakReminderDays { get; set; } = 7; // 泄漏测试提醒周期 天
    public double ProgramGasWaitMin { get; set; } = 60;   // 程序气路步骤等待其他程序释放气路的上限 分钟（超时该步骤故障、程序中止）

    // 泄漏测试（惰性气体低压保压，见 LeakViewModel）
    public double LeakTestPsi { get; set; } = 50;     // 测试压力 psi（低压，安全优先）
    public double LeakSettleMin { get; set; } = 10;   // 平衡时间 分钟
    public double LeakMeasureMin { get; set; } = 30;  // 测量时间 分钟
    public System.DateTime? LastLeakTestUtc { get; set; }   // 上次完成泄漏测试的时间（提醒用）
}
