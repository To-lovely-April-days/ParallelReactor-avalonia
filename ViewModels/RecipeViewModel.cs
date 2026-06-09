using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private void Open()
    {
        BuildChannels();
        SelectedRecipe = Recipes.Count > 0 ? Recipes[0] : null;
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
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

    [RelayCommand]
    private void Apply()
    {
        if (SelectedRecipe is not { } r) return;
        int n = 0;
        foreach (var c in Channels.Where(c => c.Picked && !c.Disabled))
        {
            var rv = _main.FindReactor(c.Id);
            if (rv is null) continue;
            rv.TSp = r.TSp;
            rv.PSp = r.PSp;
            rv.RpmSp = r.RpmSp;
            rv.Vol = r.Vol;
            rv.Blade = r.Blade;
            rv.End = r.End;
            rv.Run = r.Run;
            rv.AppliedRecipe = r.Name;
            n++;
        }
        _main.RefreshSchematic();
        if (_main.Drawer.IsOpen) _main.Drawer.RaiseAll();
        IsOpen = false;
        _main.Toast("ok", $"已将「{r.Name}」应用到 {n} 个通道");
    }

    private void BuildChannels()
    {
        foreach (var c in Channels) c.PropertyChanged -= OnPickChanged;
        Channels.Clear();
        foreach (var rv in _main.Reactors)
        {
            bool idle = rv.State == ReactorState.Idle;
            var cp = new ChannelPick
            {
                Id = rv.Id,
                Disabled = idle,
                StateText = idle ? "已停用" : rv.StateZh,
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
    private void Toggle()
    {
        if (!Disabled) Picked = !Picked;
    }

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    public IBrush PickBg => Picked && !Disabled ? B("#22E0394C") : B("#73303037");
    public IBrush PickBorder => Picked && !Disabled ? B("#e0394c") : B("#14FFFFFF");
    public IBrush NameBrush => Picked && !Disabled ? B("#f6f6f8") : B("#a4a4b0");
}
