using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using TsukiAI.Core.Models;
using MessageBox = System.Windows.MessageBox;

namespace TsukiAI.VoiceChat.Views;

public partial class MainWindow : Window
{
    private const string TtsPlaceholder = "Type text to test TTS...";
    private const double ExpandedSidebarWidth = 280;
    private const double CollapsedSidebarWidth = 26;
    private static readonly string BridgePath = ResolveBridgePath();
    private static readonly string BridgePidFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TsukiAI",
        "discord-bridge.pid");
    private static readonly HttpClient BridgePlaybackClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    private Process? _serverProcess;
    private TsukiAI.Core.Models.AppSettings _settings = TsukiAI.Core.Models.AppSettings.Default;
    private Key _voiceReceptionToggleKey = Key.F8;
    private bool _isUpdatingServerStatusToggle;
    private bool _isSidebarCollapsed;

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        LoadVoiceReceptionSettings();
        ApplyPlatformUiState();
        ApplySidebarState();
        UpdateServerStatusUi(false);

        // Auto-start bridge server on app open (only for Discord platform)
        Loaded += async (_, _) =>
        {
            if (_settings.VoicePlatform == VoiceIntegrationPlatform.Discord)
            {
                await Task.Delay(1000); // Brief delay to let UI settle
                TryStartBridgeServer(showSuccessMessage: false);
            }
        };
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        StopBridgeServerProcess();
    }

    private void OpenMainSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = new SettingsWindow(owner: this);
            settingsWindow.ShowDialog();
            LoadVoiceReceptionSettings();
            ApplyPlatformUiState();
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\n{ex.InnerException.Message}";
            MessageBox.Show(
                this,
                $"Settings could not be opened.\n\n{details}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenVoiceSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsWindow = new VoiceChatSettingsWindow { Owner = this };
            settingsWindow.ShowDialog();
            LoadVoiceReceptionSettings();
            ApplyPlatformUiState();
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\n{ex.InnerException.Message}";
            MessageBox.Show(
                this,
                $"Voice settings could not be opened.\n\n{details}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void DevLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new DevLogWindow { Owner = this };
            win.Show();
        }
        catch (Exception ex)
        {
            var details = ex.InnerException is null ? ex.Message : $"{ex.Message}\n{ex.InnerException.Message}";
            MessageBox.Show(
                this,
                $"Dev Logs could not be opened.\n\n{details}",
                "TsukiAI Voice Chat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void AllUsersFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "All Users Mode";
        FocusStatusDescription.Text = "Active · Captures everyone in the selected voice channel.";
        if (SpecificUserPanel != null)
            SpecificUserPanel.Visibility = Visibility.Collapsed;
    }

    private void AutoFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "Auto Focus Mode";
        FocusStatusDescription.Text = "Active · Automatically detects and focuses on the speaking user.";
        if (SpecificUserPanel != null)
            SpecificUserPanel.Visibility = Visibility.Collapsed;
    }

    private void SpecificUserFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "Specific User Mode";
        FocusStatusDescription.Text = "Waiting · Enter a Discord user ID and click Apply.";
        if (SpecificUserPanel != null)
            SpecificUserPanel.Visibility = Visibility.Visible;
    }

    private void ApplyFocusedUser_Click(object sender, RoutedEventArgs e)
    {
        var userId = FocusedUserIdInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            MessageBox.Show(this, "Enter a Discord user ID first.", "TsukiAI Voice Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FocusStatusTitle.Text = "Specific User Mode";
        FocusStatusDescription.Text = $"Applied · Focused user ID: {userId}";
    }

    private void TtsTestInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (TtsCharCounter == null)
        {
            return;
        }

        var length = TtsTestInput.Text == TtsPlaceholder ? 0 : TtsTestInput.Text.Length;
        TtsCharCounter.Text = $"{length} / 1000";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeButton.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeButton.Visibility = Visibility.Collapsed;
            RestoreButton.Visibility = Visibility.Visible;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StopBridgeServerProcess();
        System.Windows.Application.Current.Shutdown();
    }

    private static Key ParseToggleHotkey(string? hotkeyText, Key fallback)
    {
        var raw = (hotkeyText ?? string.Empty).Trim();
        if (Enum.TryParse<Key>(raw, true, out var key) && key != Key.None)
        {
            return key;
        }

        return fallback;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat || e.Key != _voiceReceptionToggleKey)
        {
            return;
        }

        if (VoiceReceptionToggle == null)
        {
            return;
        }

        VoiceReceptionToggle.IsChecked = !(VoiceReceptionToggle.IsChecked ?? false);
        e.Handled = true;
    }

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        _isSidebarCollapsed = !_isSidebarCollapsed;
        ApplySidebarState();
    }

    private static FrameworkElement BuildSidebarToggleIcon(double angle)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 1.5 1.5 L 12.5 1.5 L 12.5 12.5 L 1.5 12.5 Z M 4 1.5 L 4 12.5 M 9.3 4.2 L 6.8 7 L 9.3 9.8"),
            Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCF, 0xC7, 0xE6)),
            StrokeThickness = 1.5,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            RenderTransform = new System.Windows.Media.RotateTransform(angle, 7, 7)
        };

        return new System.Windows.Controls.Viewbox
        {
            Width = 18,
            Height = 18,
            Child = path
        };
    }

    private void ApplySidebarState()
    {
        if (LeftSidebarPanel == null || SidebarExpandedContent == null || SidebarCollapsedContent == null)
        {
            return;
        }

        LeftSidebarPanel.Width = _isSidebarCollapsed ? 48 : 300;
        SidebarExpandedContent.Visibility = _isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarCollapsedContent.Visibility = _isSidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPlatformUiState()
    {
        var isVrChat = _settings.VoicePlatform == VoiceIntegrationPlatform.VrChat;
        var isOther = _settings.VoicePlatform == VoiceIntegrationPlatform.Other;

        if (VoiceChatTitleText != null)
        {
            VoiceChatTitleText.Text = isVrChat ? "🎤 Voice Chat · VRChat" : (isOther ? "🎤 Voice Chat" : "🎤 Voice Chat · Discord");
        }

        if (ServerStatusText != null)
        {
            var serverStatusParent = ServerStatusText.Parent as FrameworkElement;
            if (serverStatusParent != null)
            {
                serverStatusParent.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        if (AllUsersFocusRadio != null)
        {
            AllUsersFocusRadio.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AutoFocusRadio != null)
        {
            AutoFocusRadio.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SpecificUserFocusRadio != null)
        {
            SpecificUserFocusRadio.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SpecificUserPanel != null)
        {
            SpecificUserPanel.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : SpecificUserPanel.Visibility;
        }

        if (FocusStatusPanel != null)
        {
            FocusStatusPanel.Visibility = (isVrChat || isOther) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (VrChatRoutePanel != null)
        {
            VrChatRoutePanel.Visibility = isVrChat ? Visibility.Visible : Visibility.Collapsed;
        }

        if (VrChatRouteDescription != null && isVrChat)
        {
            VrChatRouteDescription.Text =
                $"Set your voice output to the routed device or virtual cable, then configure VRChat to listen on that input path. OSC defaults: {_settings.VrChatOscHost}:{_settings.VrChatOscInputPort} in, {_settings.VrChatOscOutputPort} out.";
        }

        if (PlatformTipText != null)
        {
            PlatformTipText.Text = isVrChat
                ? "Tip: for VRChat, use a virtual cable for Tsuki output and keep local microphone mode enabled."
                : (isOther ? "Standalone mode: Voice output to selected audio device only."
                    : "Tip: Right-click user in Discord -> Copy ID");
        }

        if (PlatformPlaybackButton != null)
        {
            PlatformPlaybackButton.Content = isVrChat ? "Play to VRChat Output" : (isOther ? "Test Output" : "Play in Discord");
        }
    }
}
