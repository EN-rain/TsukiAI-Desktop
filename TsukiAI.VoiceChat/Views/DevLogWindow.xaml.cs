using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Views;

public partial class DevLogWindow : Window
{
    private ScrollViewer? _logScrollViewer;

    public DevLogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshLog();
            DevLog.LogUpdated += OnLogUpdated;
        };
        Closed += (_, _) => DevLog.LogUpdated -= OnLogUpdated;
    }

    private void OnLogUpdated()
    {
        if (Dispatcher.CheckAccess())
        {
            RefreshLog();
            return;
        }

        Dispatcher.BeginInvoke(RefreshLog);
    }

    private void RefreshLog()
    {
        var text = DevLog.GetText();
        var viewer = GetLogScrollViewer();
        var previousOffset = viewer?.VerticalOffset ?? 0;
        var previousScrollableHeight = viewer?.ScrollableHeight ?? 0;
        var shouldStickToBottom = viewer is null || previousScrollableHeight <= 0 || previousOffset >= previousScrollableHeight - 6;

        LogBox.Text = text;

        Dispatcher.BeginInvoke(() =>
        {
            var sv = GetLogScrollViewer();
            if (sv is null)
            {
                if (shouldStickToBottom)
                {
                    LogBox.ScrollToEnd();
                }
                return;
            }

            if (shouldStickToBottom)
            {
                sv.ScrollToEnd();
            }
            else
            {
                sv.ScrollToVerticalOffset(previousOffset);
            }
        }, DispatcherPriority.Background);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        DevLog.Clear();
        RefreshLog();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = LogBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        System.Windows.Clipboard.SetText(text);
        var originalTooltip = CopyButton.ToolTip?.ToString();
        CopyButton.ToolTip = "Copied";

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.ToolTip = originalTooltip ?? "Copy log";
        };
        timer.Start();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private ScrollViewer? GetLogScrollViewer()
    {
        if (_logScrollViewer is not null)
        {
            return _logScrollViewer;
        }

        _logScrollViewer = FindVisualChild<ScrollViewer>(LogBox);
        return _logScrollViewer;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
