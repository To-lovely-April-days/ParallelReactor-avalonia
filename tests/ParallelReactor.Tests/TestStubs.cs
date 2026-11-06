// 链接 Models/AppSettings.cs 所需的最小替身：ApproachParams 定义在 Models/HeatApproach.cs，
// 那个文件引用 CommunityToolkit（ObservableObject），测试工程不链接它。签名须与原定义一致。
namespace ParallelReactor.Models;

public readonly record struct ApproachParams(double Delta, double Rate, double SettleBand, double SettleSec,
    double FastRate, double FinalZone);
