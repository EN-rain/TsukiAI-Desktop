using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TsukiAI.Core.Services;

namespace TsukiAI.VoiceChat.Views;

public partial class DevLogWindow : Window
{
    private ScrollViewer? _logScrollViewer;
    private bool _followTail = true;
    private bool _isProgrammaticScroll;

    public DevLogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshLog();
            AttachScrollTracking();
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
        var shouldStickToBottom = _followTail || viewer is null;

        LogTextBlock.Text = text;

        Dispatcher.BeginInvoke(() =>
        {
            var sv = GetLogScrollViewer();
            if (sv is null)
            {
                return;
            }

            if (shouldStickToBottom)
            {
                _isProgrammaticScroll = true;
                sv.ScrollToEnd();
                _isProgrammaticScroll = false;
            }
            else
            {
                _isProgrammaticScroll = true;
                sv.ScrollToVerticalOffset(previousOffset);
                _isProgrammaticScroll = false;
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
        var text = LogTextBlock.Text ?? string.Empty;
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
        _logScrollViewer ??= LogScrollViewer;
        return _logScrollViewer;
    }

    private void AttachScrollTracking()
    {
        var viewer = GetLogScrollViewer();
        if (viewer is null)
        {
            return;
        }

        viewer.ScrollChanged -= LogScrollViewer_ScrollChanged;
        viewer.ScrollChanged += LogScrollViewer_ScrollChanged;
        _followTail = IsNearBottom(viewer);
    }

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll || sender is not ScrollViewer viewer)
        {
            return;
        }

        _followTail = IsNearBottom(viewer);
    }

    private static bool IsNearBottom(ScrollViewer viewer)
    {
        return viewer.ScrollableHeight <= 0 || viewer.VerticalOffset >= viewer.ScrollableHeight - 24;
    }
}
