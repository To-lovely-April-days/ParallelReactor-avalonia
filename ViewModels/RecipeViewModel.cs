using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>配方应用：选择配方 → 选择目标通道 → 应用 SP。</summary>
public partial class RecipeViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private Recipe? _selectedRecipe;

    public System.Collections.Generic.List<Recipe> Recipes => _main.Recipes;
    public ObservableCollection<ChannelPick> Channels { get; } = new();

    public int SelectedCount => Channels.Count(c => c.Picked && !c.Disabled);
    public bool HasRecipe => SelectedRecipe != null;
    public bool CanApply => HasRecipe && SelectedCount > 0;
    public string ApplyText => $"应用到选中通道（{SelectedCount}）";

    public RecipeViewModel(MainViewModel main) => _main = main;

    partial void OnSelectedRecipeChanged(Recipe? value)
    {
        OnPropertyChanged(nameof(HasRecipe));
        OnPropertyChanged(nameof(CanApply));
    }

    [RelayCommand]
    [ViewEntry("打开配方面板")]
    private void Open()
    {
        BuildChannels();
        SelectedRecipe = Recipes.Count > 0 ? Recipes[0] : null;
        IsOpen = true;
    }

    [RelayCommand]
    [ViewEntry("关闭配方面板")]
    private void Close() => IsOpen = false;

    [RelayCommand]
    [ViewEntry("快速勾选目标通道（只改面板选择）")]
    private void QuickPick(string mode)
    {
        foreach (var c in Channels)
        {
            c.Picked = mode switch
            {
                "all" => !c.Disabled,
                "active" => !c.Disabled && c.Running,
                "1-4" => !c.Disabled && c.Id <= 4,
                "5-8" => !c.Disabled && c.Id >= 5,
                _ => false,
            };
        }
        RaiseCount();
    }

    /// <summary>把选中的配方应用到勾选的通道（守卫与提示文字在 OperatorCommands.TryApplyRecipe）。成功后关闭面板。</summary>
    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryApplyRecipe))]
    private void Apply()
    {
        if (SelectedRecipe is not { } r) return;
        var t = _main.Ops.TryApplyRecipe(MainViewModel.Src, r.Name,
            Channels.Where(c => c.Picked && !c.Disabled).Select(c => c.Id).ToList());
        if (t.IsCompletedSuccessfully && t.Result.Ok) IsOpen = false;
        _main.Local(t);
    }

    private void BuildChannels()
    {
        foreach (var c in Channels) c.PropertyChanged -= OnPickChanged;
        Channels.Clear();
        foreach (var rv in _main.Reactors)
        {
            bool idle = rv.State == ReactorState.Idle;
            bool prog = _main.Program.RunningRv == rv.Id;   // 正在执行程序的通道不可套配方（程序按通道给定值判断到位）
            var cp = new ChannelPick
            {
                Id = rv.Id,
                Disabled = idle || prog,
                StateText = idle ? "已停用" : prog ? "程序执行中" : rv.StateZh,
                Running = rv.State is not (ReactorState.Idle or ReactorState.Done),
            };
            cp.PropertyChanged += OnPickChanged;
            Channels.Add(cp);
        }
        RaiseCount();
    }

    private void OnPickChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelPick.Picked)) RaiseCount();
    }

    private void RaiseCount()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ApplyText));
    }
}

/// <summary>配方应用时的一个可选通道。</summary>
public partial class ChannelPick : ObservableObject
{
    public int Id { get; init; }
    public bool Disabled { get; init; }
    public bool Running { get; init; }
    public string StateText { get; init; } = "";

    [ObservableProperty] private bool _picked;

    public string RvName => $"RV{Id}";
    public double CellOpacity => Disabled ? 0.4 : 1.0;

    partial void OnPickedChanged(bool value)
    {
        OnPropertyChanged(nameof(PickBg));
        OnPropertyChanged(nameof(PickBorder));
        OnPropertyChanged(nameof(NameBrush));
    }

    [RelayCommand]
    [ViewEntry("勾选 / 取消勾选目标通道（只改面板选择）")]
    private void Toggle()
    {
        if (!Disabled) Picked = !Picked;
    }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public IBrush PickBg => Picked && !Disabled ? B("#22E0394C") : B("#FFF1F2F4");
    public IBrush PickBorder => Picked && !Disabled ? B("#e0394c") : B("#14000000");
    public IBrush NameBrush => Picked && !Disabled ? B("#17171c") : B("#54545e");
}
