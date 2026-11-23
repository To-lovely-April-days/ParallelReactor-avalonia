using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>
/// 模板库里的一个模板（设计稿 research/fusion-port/tpl-design 方向 A，用户 2026-11-23「按 A 做」）：新建程序时的起点，不是程序 ——
/// 不能下发、不能直接改；「用它新建」得到一份独立的程序，之后和模板无关。
/// <list type="bullet">
/// <item><b>内置模板</b>（<see cref="BuiltIn"/> = true）：随软件提供（<see cref="ProgramTemplates.BuiltIn"/>），不能改名、不能删；软件升级时跟着更新。</item>
/// <item><b>我的模板</b>：从自己的程序「存为模板」得到，存在本机（App 的 ProgramTemplateStore：templates.json），可以改名、删除。</item>
/// </list>
/// 只保存步骤和参数（<see cref="Program"/> 的步骤 + 适用型号），不含样品名、通道、溶液体积。
/// </summary>
public sealed class PcTemplate
{
    public PcTemplate(PcProgram program, bool builtIn, string note = "", string from = "")
    {
        Program = program;
        BuiltIn = builtIn;
        Note = note;
        From = from;
    }

    /// <summary>步骤、名字、适用型号、存入时刻与人（Modified / By）。Draft 不用。</summary>
    public PcProgram Program { get; }
    public bool BuiltIn { get; }
    /// <summary>说明（内置：这个流程做什么；我的模板：存入时填的，可以空）。</summary>
    public string Note { get; set; }
    /// <summary>我的模板：来自哪个程序（名字，只作说明）。</summary>
    public string From { get; }

    public string Id => Program.Id;
    public string Name { get => Program.Name; set => Program.Name = value; }
    public string Model => Program.Model;

    /// <summary>名字最长多少个字符。</summary>
    public const int NameMax = 40;
    /// <summary>说明最长多少个字符。</summary>
    public const int NoteMax = 120;

    /// <summary>
    /// 用这个模板新建一份程序：步骤逐个复制（之后改程序不影响模板），草稿、型号 = 当前设备的型号。
    /// </summary>
    public PcProgram Instantiate(string id, string name, string model, DateTime now, string by = "本机")
    {
        var p = new PcProgram { Id = id, Name = name, Model = model, Modified = now, By = by, Draft = true };
        p.Steps.AddRange(Program.Steps.Select(s => s.Clone()));
        if (p.Steps.Count == 0 || p.Steps[^1].Type != StepType.End) p.Steps.Add(ProgramTemplates.End());
        return p;
    }

    /// <summary>把一个程序存成「我的模板」：步骤逐个复制，型号跟随程序。</summary>
    public static PcTemplate FromProgram(PcProgram source, string id, string name, string note, DateTime now, string by)
    {
        var p = new PcProgram { Id = id, Name = name, Model = source.Model, Modified = now, By = by, Draft = false };
        p.Steps.AddRange(source.Steps.Select(s => s.Clone()));
        return new PcTemplate(p, false, note, source.Name);
    }

    /// <summary>名字的问题（null = 可以）：不能空、不能太长、不能和别的模板重名（<paramref name="selfId"/> = 正在改名的那个）。</summary>
    public static string? NameError(string? name, IEnumerable<PcTemplate> all, string? selfId = null)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return "请填模板名称";
        if (n.Length > NameMax) return "名称最多 " + NameMax + " 个字符";
        if (all.Any(t => t.Id != selfId && string.Equals(t.Name.Trim(), n, StringComparison.OrdinalIgnoreCase))) return "已有同名模板 · 换一个名字";
        return null;
    }

    /// <summary>程序名的问题（新建程序窗口）：不能空、不能太长。</summary>
    public static string? ProgramNameError(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) return "请填程序名称";
        if (n.Length > NameMax) return "名称最多 " + NameMax + " 个字符";
        return null;
    }
}
