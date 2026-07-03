namespace ParallelReactor.Models;

/// <summary>全局可配置参数与报警阈值（设置页编辑，各处引用）。</summary>
public class AppSettings
{
    // 可设置参数范围（上限）
    public double TMaxSp { get; set; } = 200;     // 温度 SP 上限 °C
    public double PMaxSp { get; set; } = 500;     // 压力 SP 上限 psi
    public double RpmMax { get; set; } = 2000;    // 搅拌转速上限 rpm
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

    // 报警阈值
    public double OverPressure { get; set; } = 510;   // 超压报警 psi
    public double OverTemp { get; set; } = 250;       // 超温报警 °C
    public double HeatTimeout { get; set; } = 20;     // 升温超时 分钟
    public double PressDeviation { get; set; } = 8;   // 压力偏离 SP 报警 psi
    public double LeakRate { get; set; } = 2;         // 泄漏率阈值 psi/hr
    public double LeakReminderDays { get; set; } = 7; // 泄漏测试提醒周期 天
}
