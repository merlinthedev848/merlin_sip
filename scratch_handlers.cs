    private async void SettingsAccountButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsAccountTab;
        await RefreshConnectionDiagnosticsAsync();
        await EnsureConnectionReadyAsync();
    }

    private void SettingsGeneralButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsGeneralTab;
    }

    private void SettingsHandlingButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsHandlingTab;
    }

    private void SettingsDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.SelectedItem = SettingsDevicesTab;
    }
