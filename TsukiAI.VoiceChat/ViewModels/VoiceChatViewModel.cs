using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using TsukiAI.Core.Services;
using TsukiAI.VoiceChat.Services;

namespace TsukiAI.VoiceChat.ViewModels;

public sealed class VoiceChatViewModel : INotifyPropertyChanged
{
    private static readonly System.Windows.Media.Brush ActiveStatusBrush = CreateFrozenBrush(0x2D, 0xBE, 0x74);
    private static readonly System.Windows.Media.Brush InactiveStatusBrush = CreateFrozenBrush(0xDC, 0x6B, 0x6B);
    private const string ListeningStatusText = "Voice pipeline running. Tsuki is listening.";

    private readonly VoiceConversationPipeline _pipeline;
    private readonly RelayCommand _startCommand;
    private readonly RelayCommand _stopCommand;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _typingAnimationTimer;
    private readonly DispatcherTimer _assistantLineTypingTimer;
    private readonly DispatcherTimer _speechIndicatorTimer;
    private readonly Queue<PendingAssistantLine> _pendingAssistantLines = new();
    private readonly Queue<PendingSpeechItem> _pendingSpeechItems = new();
    private readonly ObservableCollection<string> _activityFeed = new();
    private string _statusText = "Ready to start voice pipeline.";
    private bool _isRunning;
    private bool _assistantTyping;
    private int _typingDotCount;
    private string? _activeAssistantLinePrefix;
    private string _activeAssistantLineText = string.Empty;
    private int _activeAssistantLineIndex;
    private bool _isAssistantLineAnimating;
    private string _sessionDurationText = "00:00";
    private string _currentTime = DateTime.Now.ToString("HH:mm:ss");
    private string _activityFeedText = string.Empty;
    private DateTimeOffset? _sessionStartedAt;
    private bool _hasRealConversationActivity;
    private PendingSpeechItem? _activeSpeechItem;
    private DateTimeOffset _activeSpeechEndsAt;
    private string _speechIndicatorText = string.Empty;
    private bool _isSpeechIndicatorVisible;

    public VoiceChatViewModel(
        VoiceConversationPipeline pipeline)
    {
        _pipeline = pipeline;

        _startCommand = new RelayCommand(Start, () => !IsRunning);
        _stopCommand = new RelayCommand(Stop, () => IsRunning);
        StartCommand = _startCommand;
        StopCommand = _stopCommand;

        ActivityFeed = new ReadOnlyObservableCollection<string>(_activityFeed);

        _sessionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionTimer.Tick += (_, _) => UpdateSessionDuration();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => CurrentTime = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();

        _typingAnimationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _typingAnimationTimer.Tick += (_, _) => UpdateTypingStatusText();

        _assistantLineTypingTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(14)
        };
        _assistantLineTypingTimer.Tick += (_, _) => OnAssistantLineTypingTick();

        _speechIndicatorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _speechIndicatorTimer.Tick += (_, _) => OnSpeechIndicatorTick();

        AppendActivity("Voice chat is ready. Press Start to begin listening.");
        _pipeline.TurnCompleted += OnTurnCompleted;
        _pipeline.AssistantTypingChanged += OnAssistantTypingChanged;
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SessionState));
            OnPropertyChanged(nameof(StatusAccentBrush));
            _startCommand.RaiseCanExecuteChanged();
            _stopCommand.RaiseCanExecuteChanged();
        }
    }

    public string SessionState => IsRunning ? "Live Session" : "Offline";
    public System.Windows.Media.Brush StatusAccentBrush => IsRunning ? ActiveStatusBrush : InactiveStatusBrush;

    public string SessionDurationText
    {
        get => _sessionDurationText;
        private set
        {
            if (_sessionDurationText == value) return;
            _sessionDurationText = value;
            OnPropertyChanged();
        }
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ReadOnlyObservableCollection<string> ActivityFeed { get; }
    public string ModelName => "Voice Pipeline";

    public string CurrentTime
    {
        get => _currentTime;
        private set
        {
            if (_currentTime == value) return;
            _currentTime = value;
            OnPropertyChanged();
        }
    }

    public string ActivityFeedText
    {
        get => _activityFeedText;
        private set
        {
            if (_activityFeedText == value) return;
            _activityFeedText = value;
            OnPropertyChanged();
        }
    }

    public bool IsSpeechIndicatorVisible
    {
        get => _isSpeechIndicatorVisible;
        private set
        {
            if (_isSpeechIndicatorVisible == value) return;
            _isSpeechIndicatorVisible = value;
            OnPropertyChanged();
        }
    }

    public string SpeechIndicatorText
    {
        get => _speechIndicatorText;
        private set
        {
            if (_speechIndicatorText == value) return;
            _speechIndicatorText = value;
            OnPropertyChanged();
        }
    }

    private void Start()
    {
        if (IsRunning) return;

        _pipeline.Start();
        _sessionStartedAt = DateTimeOffset.Now;
        SessionDurationText = "00:00";
        UpdateSessionDuration();
        _sessionTimer.Start();

        IsRunning = true;
        _assistantTyping = false;
        _typingDotCount = 0;
        StatusText = ListeningStatusText;
        AppendActivity($"[{DateTime.Now:HH:mm:ss}] Session started.");
    }

    private void Stop()
    {
        if (!IsRunning) return;

        _pipeline.Stop();
        _sessionTimer.Stop();
        UpdateSessionDuration();
        _assistantTyping = false;
        _typingDotCount = 0;
        _typingAnimationTimer.Stop();
        _assistantLineTypingTimer.Stop();
        _speechIndicatorTimer.Stop();
        _pendingAssistantLines.Clear();
        _pendingSpeechItems.Clear();
        _isAssistantLineAnimating = false;
        _activeAssistantLinePrefix = null;
        _activeAssistantLineText = string.Empty;
        _activeAssistantLineIndex = 0;
        _activeSpeechItem = null;
        IsSpeechIndicatorVisible = false;
        SpeechIndicatorText = string.Empty;

        IsRunning = false;
        StatusText = $"Voice pipeline stopped. Last session duration: {SessionDurationText}.";
        AppendActivity($"[{DateTime.Now:HH:mm:ss}] Session stopped.");
        _sessionStartedAt = null;
    }

    private void UpdateSessionDuration()
    {
        if (_sessionStartedAt is null)
        {
            SessionDurationText = "00:00";
            return;
        }

        var elapsed = DateTimeOffset.Now - _sessionStartedAt.Value;
        SessionDurationText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    private void AppendActivity(string entry)
    {
        _activityFeed.Add(entry);
        while (_activityFeed.Count > 50)
        {
            _activityFeed.RemoveAt(0);
        }
        UpdateActivityFeedText();
    }

    private void OnAssistantTypingChanged(bool isTyping)
    {
        void UpdateUi()
        {
            _assistantTyping = IsRunning && isTyping;
            if (_assistantTyping)
            {
                _typingDotCount = 0;
                _typingAnimationTimer.Start();
                UpdateTypingStatusText();
                UpdateSpeechIndicatorText();
            }
            else
            {
                _typingDotCount = 0;
                _typingAnimationTimer.Stop();
                if (_activeSpeechItem is null)
                {
                    IsSpeechIndicatorVisible = false;
                    SpeechIndicatorText = string.Empty;
                    if (IsRunning)
                    {
                        StatusText = ListeningStatusText;
                    }
                }
                else
                {
                    UpdateSpeechIndicatorText();
                }
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            UpdateUi();
            return;
        }

        _ = dispatcher.BeginInvoke(UpdateUi, DispatcherPriority.Background);
    }

    private void UpdateTypingStatusText()
    {
        if (!IsRunning || !_assistantTyping)
        {
            return;
        }

        _typingDotCount = (_typingDotCount % 3) + 1;
        StatusText = $"Voice pipeline running. Tsuki is typing{new string('.', _typingDotCount)}";
        UpdateSpeechIndicatorText();
    }

    private void EnqueueAssistantLine(string now, int ttsSec, string responseText)
    {
        _pendingAssistantLines.Enqueue(new PendingAssistantLine(now, ttsSec, responseText));
        if (!_isAssistantLineAnimating)
        {
            StartNextAssistantLineAnimation();
        }
    }

    private void StartNextAssistantLineAnimation()
    {
        if (_pendingAssistantLines.Count == 0)
        {
            _isAssistantLineAnimating = false;
            _assistantLineTypingTimer.Stop();
            return;
        }

        var next = _pendingAssistantLines.Dequeue();
        _activeAssistantLinePrefix = $"[{next.Now}] Tsuki [TTS, {next.TtsSec}s]: ";
        _activeAssistantLineText = next.ResponseText ?? string.Empty;
        _activeAssistantLineIndex = 0;
        _isAssistantLineAnimating = true;

        AppendActivity(_activeAssistantLinePrefix);

        if (_activeAssistantLineText.Length == 0)
        {
            _assistantLineTypingTimer.Stop();
            _isAssistantLineAnimating = false;
            StartNextAssistantLineAnimation();
            return;
        }

        _assistantLineTypingTimer.Start();
    }

    private void OnAssistantLineTypingTick()
    {
        if (!_isAssistantLineAnimating || _activeAssistantLinePrefix is null)
        {
            _assistantLineTypingTimer.Stop();
            return;
        }

        _activeAssistantLineIndex = Math.Min(_activeAssistantLineText.Length, _activeAssistantLineIndex + 2);

        var visible = _activeAssistantLineText[.._activeAssistantLineIndex];
        var suffix = _activeAssistantLineIndex < _activeAssistantLineText.Length ? "▌" : string.Empty;
        ReplaceLastActivityEntry(_activeAssistantLinePrefix + visible + suffix);

        if (_activeAssistantLineIndex >= _activeAssistantLineText.Length)
        {
            _assistantLineTypingTimer.Stop();
            _isAssistantLineAnimating = false;
            ReplaceLastActivityEntry(_activeAssistantLinePrefix + _activeAssistantLineText);
            StartNextAssistantLineAnimation();
        }
    }

    private void ReplaceLastActivityEntry(string entry)
    {
        if (_activityFeed.Count == 0)
        {
            _activityFeed.Add(entry);
        }
        else
        {
            _activityFeed[_activityFeed.Count - 1] = entry;
        }

        UpdateActivityFeedText();
    }

    private void OnTurnCompleted(VoiceTurnEvent evt)
    {
        void UpdateUi()
        {
            if (!_hasRealConversationActivity)
            {
                _activityFeed.Clear();
                UpdateActivityFeedText();
                _hasRealConversationActivity = true;
            }

            var now = DateTime.Now.ToString("HH:mm:ss");
            var totalSec = Math.Max(1, (int)Math.Round(evt.TotalMs / 1000.0));
            var ttsSec = Math.Max(1, (int)Math.Round(evt.TtsMs / 1000.0));
            AppendActivity($"[{now}] Rain [Total, {totalSec}s]: {evt.InputText}");
            EnqueueAssistantLine(now, ttsSec, evt.ResponseText);
            EnqueueSpeechItem(new PendingSpeechItem("Tsuki reply", ttsSec));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            UpdateUi();
            return;
        }

        _ = dispatcher.BeginInvoke(UpdateUi, DispatcherPriority.Background);
    }

    public void NotifyManualTtsQueued(string text)
    {
        void UpdateUi()
        {
            var estimatedSeconds = EstimateSpeechSeconds(text);
            EnqueueSpeechItem(new PendingSpeechItem("Manual TTS", estimatedSeconds));
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            UpdateUi();
            return;
        }

        _ = dispatcher.BeginInvoke(UpdateUi, DispatcherPriority.Background);
    }

    private void EnqueueSpeechItem(PendingSpeechItem item)
    {
        _pendingSpeechItems.Enqueue(item);
        if (_activeSpeechItem is null)
        {
            StartNextSpeechItem();
            return;
        }

        UpdateSpeechIndicatorText();
    }

    private void StartNextSpeechItem()
    {
        if (_pendingSpeechItems.Count == 0)
        {
            _activeSpeechItem = null;
            _speechIndicatorTimer.Stop();
            if (_assistantTyping)
            {
                UpdateSpeechIndicatorText();
                return;
            }

            IsSpeechIndicatorVisible = false;
            SpeechIndicatorText = string.Empty;
            if (IsRunning)
            {
                StatusText = ListeningStatusText;
            }
            return;
        }

        _activeSpeechItem = _pendingSpeechItems.Dequeue();
        _activeSpeechEndsAt = DateTimeOffset.Now.AddSeconds(Math.Max(1, _activeSpeechItem.DurationSeconds));
        _speechIndicatorTimer.Start();
        UpdateSpeechIndicatorText();
    }

    private void OnSpeechIndicatorTick()
    {
        if (_activeSpeechItem is null)
        {
            _speechIndicatorTimer.Stop();
            return;
        }

        if (DateTimeOffset.Now >= _activeSpeechEndsAt)
        {
            StartNextSpeechItem();
            return;
        }

        UpdateSpeechIndicatorText();
    }

    private void UpdateSpeechIndicatorText()
    {
        if (_assistantTyping)
        {
            var queued = _pendingSpeechItems.Count + (_activeSpeechItem is null ? 0 : 1);
            SpeechIndicatorText = queued > 0
                ? $"Tsuki is preparing speech. {queued} item(s) queued."
                : "Tsuki is preparing speech...";
            IsSpeechIndicatorVisible = true;
            return;
        }

        if (_activeSpeechItem is null)
        {
            IsSpeechIndicatorVisible = false;
            SpeechIndicatorText = string.Empty;
            return;
        }

        var remaining = Math.Max(1, (int)Math.Ceiling((_activeSpeechEndsAt - DateTimeOffset.Now).TotalSeconds));
        var queuedCount = _pendingSpeechItems.Count;
        SpeechIndicatorText = queuedCount > 0
            ? $"{_activeSpeechItem.Label} speaking... {remaining}s left, {queuedCount} queued next."
            : $"{_activeSpeechItem.Label} speaking... {remaining}s left.";
        IsSpeechIndicatorVisible = true;
    }

    private static int EstimateSpeechSeconds(string text)
    {
        var normalized = (text ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Ceiling(normalized.Length / 12.0));
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var color = System.Windows.Media.Color.FromRgb(r, g, b);
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void UpdateActivityFeedText()
    {
        ActivityFeedText = string.Join(Environment.NewLine, _activityFeed);
    }

    private sealed record PendingAssistantLine(string Now, int TtsSec, string ResponseText);
    private sealed record PendingSpeechItem(string Label, int DurationSeconds);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
