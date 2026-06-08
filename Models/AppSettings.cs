namespace ParallelReactor.Models;

/// <summary>全局可配置参数与报警阈值（设置页编辑，各处引用）。</summary>
public class AppSettings
{
    // 可设置参数范围（上限）
    public double TMaxSp { get; set; } = 200;     // 温度 SP 上限 °C
    public double PMaxSp { get; set; } = 500;     // 压力 SP 上限 psi
    public double RpmMax { get; set; } = 2000;    // 搅拌转速上限 rpm
    public double VolMax { get; set; } = 50;      // 溶液体积上限 mL

    // 报警阈值
    public double OverPressure { get; set; } = 510;   // 超压报警 psi
    public double OverTemp { get; set; } = 250;       // 超温报警 °C
    public double HeatTimeout { get; set; } = 20;     // 升温超时 分钟
    public double PressDeviation { get; set; } = 8;   // 压力偏离 SP 报警 psi
    public double LeakRate { get; set; } = 2;         // 泄漏率阈值 psi/hr
    public double LeakReminderDays { get; set; } = 7; // 泄漏测试提醒周期 天
}
