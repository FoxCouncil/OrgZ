// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OrgZ.Views;

public partial class MessageLogDialog : Window
{
    private ObservableCollection<string> _messages = [];

    public MessageLogDialog()
    {
        InitializeComponent();
    }

    public MessageLogDialog(ObservableCollection<string> messages, string title = "Messages")
    {
        InitializeComponent();

        _messages = messages;
        Title = title;
        HeaderText.Text = title;

        MessageList.ItemsSource = _messages;
        UpdateCount();

        _messages.CollectionChanged += (s, e) => UpdateCount();

        if (title.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            HeaderIcon.Value = "fa-solid fa-circle-exclamation";
            HeaderIcon.Foreground = Avalonia.Media.Brushes.OrangeRed;
        }
    }

    private void UpdateCount()
    {
        CountText.Text = _messages.Count > 0 ? $"({_messages.Count})" : "";
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _messages.Clear();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
