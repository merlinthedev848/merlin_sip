    private void ContactTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveContactButton_Click(sender, new RoutedEventArgs());
        }
    }
