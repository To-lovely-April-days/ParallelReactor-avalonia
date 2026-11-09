using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页用的演示值（同样稿 freeze=1 时 #/gallery 的数据：1号机 = HT-PR8 · 现场，RV1 反应中 119.8 ℃ / 0.349 MPa）。
/// 型号标签取自 Core 的内置型号表（示例型号，参数为假设，D2）；读数用 Core.Fmt 格式化（与样稿 PR.fmt 逐字相同）。
/// M2 起由演示数据 DemoFleet 提供，这里只给 M1 的陈列页。
/// </summary>
public static class GalleryDemo
{
    private static string Label(string key, string fallback) => ModelProfiles.Embedded.Get(key)?.Label ?? fallback;

    /// <summary>1号机的型号（PR8 装了 4 路的现场变体）。</summary>
    public static string Model1 => Label("PR8-4", "HT-PR8 · 现场");
    public static string Model2 => Label("PR4", "HT-PR4");
    public static string Model3 => Label("PR16", "HT-PR16");

    public static string TempPv => Fmt.Temp(119.8);
    public static string TempSp => Fmt.Temp(120.0);
    public static string PressPv => Fmt.Mpa(0.349);
    public static string PressPsi => Fmt.Psi(0.349) + " psi";
}
