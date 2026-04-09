// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OrgZ.ViewModels;

namespace OrgZ.Controls;

public partial class QueuePanel : UserControl
{
    private const string QueueDragFormat = "OrgZ.QueueIndex";

    private Point? _pressOrigin;
    private int _pressIndex = -1;

    public QueuePanel()
    {
        InitializeComponent();

        QueueListBox.AddHandler(PointerPressedEvent, QueueListBox_PointerPressed, RoutingStrategies.Tunnel);
        QueueListBox.AddHandler(PointerMovedEvent, QueueListBox_PointerMoved, RoutingStrategies.Tunnel);
        QueueListBox.AddHandler(PointerReleasedEvent, QueueListBox_PointerReleased, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(QueueListBox, true);
        QueueListBox.AddHandler(DragDrop.DragOverEvent, QueueListBox_DragOver);
        QueueListBox.AddHandler(DragDrop.DropEvent, QueueListBox_Drop);
    }

    private void RemoveItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is MediaItem item && DataContext is MainWindowViewModel vm)
        {
            var index = vm.PlaybackContextUpcoming?.IndexOf(item) ?? -1;
            if (index >= 0)
            {
                vm.RemoveFromQueue(index);
            }
        }
    }

    private void QueueListBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueueListBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Don't initiate drag if user clicked the X button
        if (e.Source is Visual src && src.FindAncestorOfType<Button>() != null)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item == null)
        {
            return;
        }

        _pressIndex = QueueListBox.IndexFromContainer(item);
        _pressOrigin = e.GetPosition(QueueListBox);
    }

    private async void QueueListBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressOrigin == null || _pressIndex < 0)
        {
            return;
        }

        if (!e.GetCurrentPoint(QueueListBox).Properties.IsLeftButtonPressed)
        {
            _pressOrigin = null;
            _pressIndex = -1;
            return;
        }

        var current = e.GetPosition(QueueListBox);
        var dy = current.Y - _pressOrigin.Value.Y;
        var dx = current.X - _pressOrigin.Value.X;
        if ((dx * dx + dy * dy) < 25)
        {
            return;
        }

        var data = new DataObject();
        data.Set(QueueDragFormat, _pressIndex);

        var indexToDrag = _pressIndex;
        _pressOrigin = null;
        _pressIndex = -1;

        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void QueueListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressOrigin = null;
        _pressIndex = -1;
    }

    private void QueueListBox_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(QueueDragFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void QueueListBox_Drop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(QueueDragFormat))
        {
            return;
        }

        if (e.Data.Get(QueueDragFormat) is not int fromIndex)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm || vm.PlaybackContextUpcoming == null)
        {
            return;
        }

        // Determine target index based on which container the drop landed on
        var target = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        int toIndex;
        if (target != null)
        {
            toIndex = QueueListBox.IndexFromContainer(target);
        }
        else
        {
            // Dropped past the last item — append to end
            toIndex = vm.PlaybackContextUpcoming.Count - 1;
        }

        if (toIndex < 0)
        {
            toIndex = 0;
        }

        vm.MoveInQueue(fromIndex, toIndex);
        e.Handled = true;
    }
}
