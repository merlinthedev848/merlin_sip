namespace MerlinSip.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private string _notice = "Application ready.";
    private string _connectionState = "Connecting...";
    private string _presenceState = "Available";
    private string _searchText = "";

    public string Notice
    {
        get => _notice;
        set => SetProperty(ref _notice, value);
    }

    public string ConnectionState
    {
        get => _connectionState;
        set => SetProperty(ref _connectionState, value);
    }

    public string PresenceState
    {
        get => _presenceState;
        set => SetProperty(ref _presenceState, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }
}
