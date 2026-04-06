using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using TsukiAI.Core.Services;

namespace TsukiAI.Core.ViewModels;

/// <summary>
/// ViewModel for conversation history management.
/// Handles context building, compression, and persistence.
/// </summary>
public sealed class ConversationViewModel : INotifyPropertyChanged
{
    private readonly IConversationDisplay _conversationDisplay;

    private readonly List<(string role, string content)> _context = new();
    private const int MaxContextMessages = 20;

    private string _conversationText = "";
    private string _assistantName = "Tsuki";

    /// <summary>
    /// Initializes a new instance of the ConversationViewModel class.
    /// </summary>
    /// <param name="conversationDisplay">The conversation display service for formatting.</param>
    /// <exception cref="ArgumentNullException">Thrown when conversationDisplay is null.</exception>
    public ConversationViewModel(IConversationDisplay conversationDisplay)
    {
        _conversationDisplay = conversationDisplay ?? throw new ArgumentNullException(nameof(conversationDisplay));
    }

    /// <summary>
    /// Gets the formatted conversation text for display.
    /// </summary>
    public string ConversationText
    {
        get => _conversationText;
        private set
        {
            if (_conversationText != value)
            {
                _conversationText = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the assistant's name for conversation formatting.
    /// </summary>
    public string AssistantName
    {
        get => _assistantName;
        set
        {
            if (_assistantName != value)
            {
                _assistantName = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Adds a user message to the conversation history.
    /// </summary>
    /// <param name="content">The content of the user message.</param>
    public void AddUserMessage(string content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        _context.Add(("user", content));
        TryCompressConversation();
        UpdateConversationText();
    }

    /// <summary>
    /// Adds an assistant message to the conversation history.
    /// </summary>
    /// <param name="content">The content of the assistant message.</param>
    public void AddAssistantMessage(string content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        _context.Add(("assistant", content));
        TryCompressConversation();
        UpdateConversationText();
    }

    /// <summary>
    /// Adds a system message to the conversation history.
    /// </summary>
    /// <param name="content">The content of the system message.</param>
    public void AddSystemMessage(string content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        _context.Add(("system", content));
        TryCompressConversation();
        UpdateConversationText();
    }

    /// <summary>
    /// Gets the most recent messages from the conversation history.
    /// </summary>
    /// <param name="maxMessages">The maximum number of messages to return. Default is 10.</param>
    /// <returns>A read-only list of recent conversation messages.</returns>
    public IReadOnlyList<(string role, string content)> GetRecentHistory(int maxMessages = 10)
    {
        if (maxMessages <= 0)
            return Array.Empty<(string role, string content)>();

        return _context.TakeLast(maxMessages).ToList();
    }

    /// <summary>
    /// Gets the complete conversation history.
    /// </summary>
    /// <returns>A read-only list of all conversation messages.</returns>
    public IReadOnlyList<(string role, string content)> GetFullHistory()
    {
        return _context.ToList();
    }

    /// <summary>
    /// Clears all messages from the conversation history.
    /// </summary>
    public void Clear()
    {
        _context.Clear();
        UpdateConversationText();
    }

    /// <summary>
    /// Saves the current conversation history to persistent storage.
    /// Delegates to ConversationHistoryService for chat mode.
    /// </summary>
    public void SaveHistory()
    {
        try
        {
            ConversationHistoryService.SaveChatHistory(_context, _conversationText);
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[ConversationViewModel] Failed to save history: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads conversation history from persistent storage.
    /// Delegates to ConversationHistoryService for chat mode.
    /// </summary>
    public void LoadHistory()
    {
        try
        {
            var history = ConversationHistoryService.LoadChatHistory();
            if (history != null)
            {
                _context.Clear();
                foreach (var message in history.Messages)
                {
                    _context.Add((message.Role, message.Content));
                }
                UpdateConversationText();
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[ConversationViewModel] Failed to load history: {ex.Message}");
        }
    }

    /// <summary>
    /// Attempts to compress the conversation history when it exceeds the maximum message limit.
    /// Removes older messages while preserving recent context.
    /// </summary>
    private void TryCompressConversation()
    {
        if (_context.Count > MaxContextMessages)
        {
            // Keep the most recent messages, remove older ones
            var messagesToRemove = _context.Count - MaxContextMessages;
            _context.RemoveRange(0, messagesToRemove);

            DevLog.WriteLine($"[ConversationViewModel] Compressed conversation: removed {messagesToRemove} older messages");
        }
    }

    /// <summary>
    /// Updates the conversation text property by building formatted text from the current context.
    /// </summary>
    private void UpdateConversationText()
    {
        ConversationText = _conversationDisplay.BuildConversationText(_context);
    }

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the PropertyChanged event.
    /// </summary>
    /// <param name="name">The name of the property that changed.</param>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

