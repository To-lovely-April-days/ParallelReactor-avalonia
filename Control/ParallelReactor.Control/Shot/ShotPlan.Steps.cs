#if DEBUG
namespace ParallelReactor.Control.Shot;

/// <summary>
/// 程序步骤 v0.8（research/fusion-port/notes/STEPS-SPEC.md）的程序页截图：样稿里没有这些步骤，没有参照图，只审计 + 放大看细节。
/// <list type="bullet">
/// <item>steps-h1-*：加氢筛选模板（H1，9 步）每种步骤的参数面板（pgstep = 第几步）：气密、置换、搅拌、升温、充压、反应、放空、结束。</item>
/// <item>steps-a-cool：「平行合成程序 A」的降温（保持 · 温度 ≤ 40）；steps-ih-hold：惰性气氛加热的保持 4 h。</item>
/// <item>steps-h1-exchange0：置换次数改成 0（只吹总管，说明换成「釜里没有置换」，充到 / 放到不显示）。</item>
/// <item>steps-ox / steps-ox-check：旧格式转换来的程序（顶上「已从旧格式转换，请检查」、待改项、提示、预运行检查里的提示）。</item>
/// <item>steps-h1-leak-psi / -bar：压力和压降速率按设置的单位（psi/h、bar/h）。</item>
/// </list>
/// </summary>
public static partial class ShotPlan
{
    private static readonly Spec[] PartSteps =
    {
        new("steps-h1-leak", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=1", Both),
        new("steps-h1-exchange", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=2", Both),
        new("steps-h1-exchange0", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=2&pgset=cycles:0", Both),
        new("steps-h1-stir", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=4", Both),
        new("steps-h1-heat", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=5", Both),
        new("steps-h1-press", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=6", Both),
        new("steps-h1-react", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=7", Both),
        new("steps-h1-vent", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=8", Both),
        new("steps-h1-end", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=9", Both),
        new("steps-h1-leak-psi", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=1&punit=psi", Both),
        new("steps-h1-leak-bar", "prog", "dev=1&apop=0&pgm=one&pgp=H1&pgstep=1&punit=bar", Both),
        new("steps-a-cool", "prog", "dev=1&apop=0&pgm=one&pgp=A&pgstep=4", Both),
        new("steps-ih-hold", "prog", "dev=1&apop=0&pgm=one&pgp=IH&pgstep=5", Both),
        new("steps-hold-press", "prog", "dev=1&apop=0&pgm=one&pgp=IH&pgstep=5&pgset=until:pressBelow", Both),
        new("steps-ox", "prog", "dev=1&apop=0&pgm=one&pgp=OX&pgstep=3", Both),
        new("steps-ox-check", "prog", "dev=1&apop=0&pgm=one&pgp=OX&dialog=pgcheck", Both),
    };
}
#endif
