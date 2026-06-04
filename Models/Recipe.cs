namespace ParallelReactor.Models;

/// <summary>配方模板，对应 HTML 中 RECIPES 数组项</summary>
public class Recipe
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Tag { get; init; } = "";
    public double TSp { get; init; }
    public double PSp { get; init; }
    public int RpmSp { get; init; }
    public int Vol { get; init; }
    public string Blade { get; init; } = "桨式";
    public string End { get; init; } = "密封";
    public string Run { get; init; } = "02:00:00";
}
