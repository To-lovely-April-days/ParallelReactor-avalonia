using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ParallelReactor.ViewModels;

/// <summary>系统设置界面（SETTING Tab）：左侧菜单 + 右侧配置分页。</summary>
public partial class SettingViewModel : ViewModelBase
{
    public ObservableCollection<SetPage> Pages { get; } = new();
    [ObservableProperty] private SetPage? _selected;

    public SettingViewModel()
    {
        Seed();
        Selected = Pages.Count > 0 ? Pages[0] : null;

        // 加载关于页的二维码图片
        foreach (var p in Pages)
            foreach (var s in p.Sections)
                if (s.IsAbout && !string.IsNullOrEmpty(s.QrUrl))
                    LoadQr(s);
    }

    /// <summary>加载二维码：avares:// 或相对路径走本地打包资源，http(s) 走网络。</summary>
    private static void LoadQr(SetSection s)
    {
        var src = s.QrUrl;
        if (string.IsNullOrEmpty(src)) return;

        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadHttpAsync(s);
            return;
        }

        try
        {
            var uri = src.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(src)
                : new Uri($"avares://ParallelReactor/{src.TrimStart('/')}");
            using var stream = AssetLoader.Open(uri);
            s.QrImage = new Bitmap(stream);
        }
        catch
        {
            // 资源缺失时留空，不影响其余内容
        }
    }

    private static async Task LoadHttpAsync(SetSection s)
    {
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(s.QrUrl);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            Dispatcher.UIThread.Post(() => s.QrImage = bmp);
        }
        catch
        {
            // 离线或地址无效时留空
        }
    }

    private static Geometry G(string d) => Geometry.Parse(d);

    private void Seed()
    {
        Pages.Add(new SetPage
        {
            Id = "general", Name = "通用", Title = "通用", Sub = "界面、语言、单位",
            Icon = G("M12 1v6 M12 17v6 M4.22 4.22l4.24 4.24 M15.54 15.54l4.24 4.24 M1 12h6 M17 12h6 M4.22 19.78l4.24-4.24 M15.54 8.46l4.24-4.24 M15 12a3 3 0 1 1-6 0 3 3 0 1 1 6 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("语言", "中文（简体）", "Language"),
                    SetRow.Val("压力单位", "psi（可切换至 bar）", "Pressure Unit"),
                    SetRow.Val("温度单位", "°C"),
                    SetRow.Val("主题", "深色（默认）"),
                    SetRow.Val("采样率", "3 s/样", "影响 .ear 文件大小")),
                SetSection.Card(
                    SetRow.Toggle("运行前自动检查", "每次启动前弹出 9 项检查清单", true),
                    SetRow.Toggle("注射事件自动重置计数", "检测到注射后弹气泡询问是否清零", true),
                    SetRow.Toggle("智能升温诊断", "升温超时主动建议", true)),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "channels", Name = "通道与气路", Title = "通道与气路", Sub = "8 路 RV · 3 路气源 + 1 路 Vent",
            Icon = G("M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Chip("惰性气体源", "在线 · 518 psi", trail: "N₂"),
                    SetRow.Chip("反应气 A", "在线 · 520 psi", trail: "H₂"),
                    SetRow.Chip("反应气 B", "未启用 · 515 psi", warn: true, trail: "N₂ QUENCH"),
                    SetRow.Val("排空 / 泄压", "→ 通风橱（已校验）")),
                SetSection.Card("RV 校准状态",
                    SetRow.Chip("温度校准", "8 / 8 已校准", sub: "单点 150°C 斜率校准"),
                    SetRow.Chip("压力校准", "8 / 8 已校准", sub: "500 psi 满量程"),
                    SetRow.Val("桨叶使用时长", "128 h", "建议 ≤ 350 小时"),
                    SetRow.Val("O 圈累计运行", "42 次", "压扁会自动提醒")),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "comm", Name = "通讯设置", Title = "通讯设置", Sub = "3 路独立 RS485 · Modbus RTU",
            Icon = G("M5 12.55a11 11 0 0 1 14 0 M1.42 9a16 16 0 0 1 21.16 0 M8.53 16.11a6 6 0 0 1 6.95 0 M12 20h.01"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Chip("温控模块", "在线 · 9.6 kbps", sub: "宇电 AI-8 · /dev/ttyUSB0"),
                    SetRow.Chip("IO 模块", "在线 · 9.6 kbps", sub: "艾莫迅 JY-IO16R · /dev/ttyUSB1"),
                    SetRow.Chip("驱动模块", "在线 · 115.2 kbps", sub: "雷赛 DM2C-RS432 · /dev/ttyUSB2"),
                    SetRow.Val("心跳间隔", "1 s"),
                    SetRow.Val("超时重连", "3 次失败后告警")),
                SetSection.Note("单模块中断不影响其他模块及在跑反应；关闭/重启上位机不会停止仪器中的反应（仪器侧自带状态机）。"),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "user", Name = "用户与权限", Title = "用户与权限", Sub = "管理员可控制阀门、改高级参数、停用通道",
            Icon = G("M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M16 7a4 4 0 1 1-8 0 4 4 0 1 1 8 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Chip("admin", "在线", labelBold: true, sub: "当前账户 · 管理员"),
                    SetRow.Val("operator-01", "最近 06-01 17:22", "普通操作员")),
                SetSection.Card("权限矩阵",
                    SetRow.Val("查看实时数据 / 历史曲线", "全部用户"),
                    SetRow.Val("修改通道 SP / 启停通道", "普通操作员 +"),
                    SetRow.Val("手动阀控 / 急停", "管理员"),
                    SetRow.Val("高级 PID 整定 / 通讯设置", "管理员")),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "data", Name = "数据存储", Title = "数据存储", Sub = "自动归档、自动备份、可对接外部数据库",
            Icon = G("M21 5a9 3 0 1 1-18 0 9 3 0 1 1 18 0 M3 5v14a9 3 0 0 0 18 0V5 M3 12a9 3 0 0 0 18 0"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("本地存储路径", "./runs/"),
                    SetRow.Val("已用 / 容量", "2.4 GB / 250 GB"),
                    SetRow.Val("历史运行记录", "8 条（保留 2 年）")),
                SetSection.Card("数据库对接（选配）",
                    SetRow.Chip("LIMS 系统对接", "未配置", warn: true),
                    SetRow.Chip("ELN 电子实验记录", "未配置", warn: true),
                    SetRow.Chip("企业级 SQL 数据库", "未配置", warn: true)),
            },
        });

        Pages.Add(new SetPage
        {
            Id = "about", Name = "关于", Title = "关于本系统", Sub = "霍桐仪器多通道平行反应仪控制系统",
            Icon = G("M22 12a10 10 0 1 1-20 0 10 10 0 1 1 20 0 M12 16v-4 M12 8h.01"),
            Sections =
            {
                SetSection.Card(
                    SetRow.Val("硬件型号", "HT-PR8"),
                    SetRow.Val("软件版本", "v2.4.1"),
                    SetRow.Val("固件版本", "v3.2.0 (2026-05-30)"),
                    SetRow.Val("设备序列号", "HT-PR8-2025-0042"),
                    SetRow.Val("运行时长", "1,847 小时"),
                    SetRow.Val("已完成运行", "192 次（演示）")),
                SetSection.About(
                    "关于霍桐仪器",
                    "上海霍桐实验仪器有限公司成立于 2015 年，是一家专注于反应设备及成套化工装配产线研发、制造与服务的高新技术企业。主营实验室及工业级反应设备、定制化化工成套装备产线，产品涵盖高压、高温、耐腐蚀等特种反应设备，广泛应用于医药合成、化工工艺开发、新材料研制等场景。",
                    "© 2026 上海霍桐实验仪器有限公司 · Huotong Instruments",
                    "avares://ParallelReactor/Assets/qr.png",
                    new ContactItem("电话", "021-6723-0701"),
                    new ContactItem("邮箱", "Htlab@htlab.com"),
                    new ContactItem("官网", "www.htlab.cn"),
                    new ContactItem("地址", "上海市闵行区东川路 3966 号 3 幢 2 楼")),
            },
        });
    }
}

/// <summary>一个设置分页。</summary>
public class SetPage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public Geometry Icon { get; set; } = Geometry.Parse("M6 6h12v12H6z");
    public List<SetSection> Sections { get; } = new();
}

/// <summary>设置页内的一个区块：卡片 / 提示 / 关于公司。</summary>
public partial class SetSection : ObservableObject
{
    public string? Header { get; set; }
    public string Kind { get; set; } = "card";          // card | note | about
    public List<SetRow> Rows { get; } = new();
    public string Text { get; set; } = "";              // note

    // 关于 / 公司
    public string AboutTitle { get; set; } = "";
    public string Intro { get; set; } = "";
    public List<ContactItem> Contacts { get; } = new();
    public string QrUrl { get; set; } = "";
    public string QrCaption { get; set; } = "扫码了解更多";
    public string AboutCopyright { get; set; } = "";

    private Bitmap? _qrImage;
    public Bitmap? QrImage
    {
        get => _qrImage;
        set => SetProperty(ref _qrImage, value);
    }

    public bool HasHeader => !string.IsNullOrEmpty(Header);
    public bool IsCard => Kind == "card";
    public bool IsNote => Kind == "note";
    public bool IsAbout => Kind == "about";

    public static SetSection Card(params SetRow[] rows)
    {
        var s = new SetSection { Kind = "card" };
        s.Rows.AddRange(rows);
        return s;
    }
    public static SetSection Card(string header, params SetRow[] rows)
    {
        var s = Card(rows);
        s.Header = header;
        return s;
    }
    public static SetSection Note(string text) => new() { Kind = "note", Text = text };

    public static SetSection About(string title, string intro, string copyright, string qrUrl, params ContactItem[] contacts)
    {
        var s = new SetSection { Kind = "about", AboutTitle = title, Intro = intro, AboutCopyright = copyright, QrUrl = qrUrl };
        s.Contacts.AddRange(contacts);
        return s;
    }
}

/// <summary>关于页的一条联系方式。</summary>
public class ContactItem
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public ContactItem(string label, string value) { Label = label; Value = value; }
}

/// <summary>设置卡片里的一行：值 / 徽标 / 开关。</summary>
public partial class SetRow : ObservableObject
{
    public string Label { get; set; } = "";
    public string? Sub { get; set; }
    public bool LabelBold { get; set; }
    public string Kind { get; set; } = "val";           // val | chip | toggle
    public string ValText { get; set; } = "";
    public string ChipText { get; set; } = "";
    public bool ChipWarn { get; set; }
    public string? Trail { get; set; }
    [ObservableProperty] private bool _on;

    [RelayCommand] private void Flip() => On = !On;

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    public bool IsVal => Kind == "val";
    public bool IsChip => Kind == "chip";
    public bool IsToggle => Kind == "toggle";
    public bool HasSub => !string.IsNullOrEmpty(Sub);
    public bool HasTrail => !string.IsNullOrEmpty(Trail);

    public IBrush LabelBrush => LabelBold ? B("#f6f6f8") : B("#a4a4b0");
    public FontWeight LabelWeight => LabelBold ? FontWeight.Bold : FontWeight.Normal;

    public IBrush ChipBg => ChipWarn ? B("#24F0A830") : B("#2484CC16");
    public IBrush ChipBorder => ChipWarn ? B("#4DF0A830") : B("#4D84CC16");
    public IBrush ChipFg => ChipWarn ? B("#f0a830") : B("#a3e635");

    public static SetRow Val(string label, string val, string? sub = null)
        => new() { Kind = "val", Label = label, ValText = val, Sub = sub };
    public static SetRow Chip(string label, string chip, bool warn = false, string? trail = null, string? sub = null, bool labelBold = false)
        => new() { Kind = "chip", Label = label, ChipText = chip, ChipWarn = warn, Trail = trail, Sub = sub, LabelBold = labelBold };
    public static SetRow Toggle(string label, string? sub, bool on)
        => new() { Kind = "toggle", Label = label, Sub = sub, On = on };
}
