using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace MerlinSip.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _licenseStatus = "Checking License...";

        [ObservableProperty]
        private string _connectionStatus = "Offline";

        public MainViewModel()
        {
            // Initial setup
        }
    }
}
