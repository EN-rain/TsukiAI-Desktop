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

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        LoadVoiceReceptionSettings();
        ApplyPlatformUiState();
        UpdateServerStatusUi(false);
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
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "All Users Mode";
        FocusStatusState.Text = "Active";
        FocusStatusDescription.Text = "Captures everyone in the selected voice channel.";
        if (SpecificUserPanel != null)
            SpecificUserPanel.Visibility = Visibility.Collapsed;
    }

    private void AutoFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "Auto Focus Mode";
        FocusStatusState.Text = "Active";
        FocusStatusDescription.Text = "Automatically detects and focuses on the speaking user.";
        if (SpecificUserPanel != null)
            SpecificUserPanel.Visibility = Visibility.Collapsed;
    }

    private void SpecificUserFocus_Checked(object sender, RoutedEventArgs e)
    {
        if (FocusStatusTitle == null || FocusStatusState == null || FocusStatusDescription == null)
            return;

        FocusStatusTitle.Text = "Specific User Mode";
        FocusStatusState.Text = "Waiting";
        FocusStatusDescription.Text = "Enter a Discord user ID and click Apply.";
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
        FocusStatusState.Text = "Applied";
        FocusStatusDescription.Text = $"Focused user ID: {userId}";
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

    private void ApplyPlatformUiState()
    {
        var isVrChat = _settings.VoicePlatform == VoiceIntegrationPlatform.VrChat;

        if (VoiceChatTitleText != null)
        {
            VoiceChatTitleText.Text = isVrChat ? "🎤 Voice Chat · VRChat" : "🎤 Voice Chat · Discord";
        }

        if (ServerStatusPanel != null)
        {
            ServerStatusPanel.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (UserFocusHeader != null)
        {
            UserFocusHeader.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AllUsersFocusRadio != null)
        {
            AllUsersFocusRadio.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AutoFocusRadio != null)
        {
            AutoFocusRadio.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SpecificUserFocusRadio != null)
        {
            SpecificUserFocusRadio.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (SpecificUserPanel != null)
        {
            SpecificUserPanel.Visibility = isVrChat ? Visibility.Collapsed : SpecificUserPanel.Visibility;
        }

        if (FocusStatusPanel != null)
        {
            FocusStatusPanel.Visibility = isVrChat ? Visibility.Collapsed : Visibility.Visible;
        }

        if (VrChatRoutePanel != null)
        {
            VrChatRoutePanel.Visibility = isVrChat ? Visibility.Visible : Visibility.Collapsed;
        }

        if (VrChatRouteDescription != null)
        {
            VrChatRouteDescription.Text =
                $"Set your voice output to the routed device or virtual cable, then configure VRChat to listen on that input path. OSC defaults: {_settings.VrChatOscHost}:{_settings.VrChatOscInputPort} in, {_settings.VrChatOscOutputPort} out.";
        }

        if (PlatformTipText != null)
        {
            PlatformTipText.Text = isVrChat
                ? "Tip: for VRChat, use a virtual cable for Tsuki output and keep local microphone mode enabled."
                : "Tip: Right-click user in Discord -> Copy ID";
        }

        if (PlatformPlaybackButton != null)
        {
            PlatformPlaybackButton.Content = isVrChat ? "Play to VRChat Output" : "Play in Discord";
        }
    }
}
