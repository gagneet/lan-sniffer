namespace LanInspector.UI.ViewModels;

public sealed record BpfFilterPresetViewModel(string Name, string Filter, string Description)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Description)
        ? Name
        : $"{Name} - {Description}";
}
