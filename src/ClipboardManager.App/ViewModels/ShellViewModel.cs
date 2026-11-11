using CommunityToolkit.Mvvm.ComponentModel;

namespace ClipboardManager.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private string _title = "历史";
    private string _description = string.Empty;
    private string _emptyTitle = string.Empty;
    private string _emptyDescription = string.Empty;
    private string _hint = string.Empty;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Description { get => _description; private set => SetProperty(ref _description, value); }
    public string EmptyTitle { get => _emptyTitle; private set => SetProperty(ref _emptyTitle, value); }
    public string EmptyDescription { get => _emptyDescription; private set => SetProperty(ref _emptyDescription, value); }
    public string Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    public void Show(ShellPageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Title = state.Title;
        Description = state.Description;
        EmptyTitle = state.EmptyTitle;
        EmptyDescription = state.EmptyDescription;
        Hint = state.Hint;
    }
}