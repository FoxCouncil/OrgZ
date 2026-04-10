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
    internal static readonly DataFormat<string> QueueDragFormat = DataFormat.CreateStringApplicationFormat("OrgZ.QueueIndex");
    internal static int DraggedQueueIndex = -1;

    private PointerPressedEventArgs? _pressEvent;
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
        _pressEvent = e;
    }

    private async void QueueListBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressOrigin == null || _pressIndex < 0 || _pressEvent == null)
        {
            return;
        }

        if (!e.GetCurrentPoint(QueueListBox).Properties.IsLeftButtonPressed)
        {
            ResetDragState();
            return;
        }

        var current = e.GetPosition(QueueListBox);
        var dy = current.Y - _pressOrigin.Value.Y;
        var dx = current.X - _pressOrigin.Value.X;
        if ((dx * dx + dy * dy) < 25)
        {
            return;
        }

        DraggedQueueIndex = _pressIndex;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(QueueDragFormat, "queue"));
        var pressEvent = _pressEvent;
        ResetDragState();

        await DragDrop.DoDragDropAsync(pressEvent, data, DragDropEffects.Move);
        DraggedQueueIndex = -1;
    }

    private void QueueListBox_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ResetDragState();
    }

    private void ResetDragState()
    {
        _pressOrigin = null;
        _pressIndex = -1;
        _pressEvent = null;
    }

    private void QueueListBox_DragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(QueueDragFormat))
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
        if (!e.DataTransfer.Contains(QueueDragFormat))
        {
            return;
        }

        var fromIndex = DraggedQueueIndex;
        if (fromIndex < 0)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel vm || vm.PlaybackContextUpcoming == null)
        {
            return;
        }

        var target = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        int toIndex;
        if (target != null)
        {
            toIndex = QueueListBox.IndexFromContainer(target);
        }
        else
        {
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
