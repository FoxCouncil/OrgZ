// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrgZ.Views;

public partial class SyncProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;

    public CancellationToken CancellationToken => _cts.Token;

    public int TotalStationsSynced { get; private set; }

    public List<string> Errors { get; } = [];

    public SyncProgressDialog()
    {
        InitializeComponent();

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (s, e) =>
        {
            ElapsedLabel.Text = $"Elapsed: {_stopwatch.Elapsed:mm\\:ss}";
        };

        Opened += (s, e) =>
        {
            _stopwatch.Start();
            _elapsedTimer.Start();
        };

        Closing += (s, e) =>
        {
            _elapsedTimer.Stop();
            _stopwatch.Stop();

            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        };
    }

    public void UpdateSource(string source)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SourceLabel.Text = source;
        });
    }

    public void UpdateProgress(int stationCount, string detail)
    {
        TotalStationsSynced = stationCount;

        Dispatcher.UIThread.Post(() =>
        {
            CountLabel.Text = $"{stationCount:N0} stations";
            DetailLabel.Text = detail;
        });
    }

    public void SetIndeterminate(bool indeterminate)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.IsIndeterminate = indeterminate;
        });
    }

    public void SetProgress(double value, double max)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = max;
            ProgressBar.Value = value;
        });
    }

    public void Finish(string summary)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _elapsedTimer.Stop();
            SourceLabel.Text = summary;
            CancelButton.Content = "Close";
            ProgressBar.Value = ProgressBar.Maximum;
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_cts.IsCancellationRequested && CancelButton.Content?.ToString() != "Close")
        {
            _cts.Cancel();
            SourceLabel.Text = "Cancelling...";
            CancelButton.IsEnabled = false;
        }
        else
        {
            Close();
        }
    }

    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;
}
