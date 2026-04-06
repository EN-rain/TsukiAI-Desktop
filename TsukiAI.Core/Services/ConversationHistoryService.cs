using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TsukiAI.Core.Services;

/// <summary>
/// Manages persistent conversation history for Chat and Voice Chat modes
/// </summary>
public static class ConversationHistoryService
{
    private static readonly string BaseDir = SettingsService.GetBaseDir();
    private static readonly string ChatHistoryPath = Path.Combine(BaseDir, "chat_history.json");
    private static readonly string VoiceChatHistoryPath = Path.Combine(BaseDir, "voice_chat_history.json");
    
    public class ConversationHistory
    {
        public List<ConversationMessage> Messages { get; set; } = new();
        public string DisplayText { get; set; } = "";
        public DateTime LastUpdated { get; set; } = DateTime.Now;
    }
    
    public class ConversationMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string? SpeakerId { get; set; } // Discord user ID or "AI"
        public string? SpeakerName { get; set; } // Display name for context
    }
    
    /// <summary>
    /// Saves chat mode conversation history
    /// </summary>
    public static async Task SaveChatHistoryAsync(IEnumerable<(string role, string content)> context, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = context.Select(c => new ConversationMessage 
                { 
                    Role = c.role, 
                    Content = c.content,
                    Timestamp = DateTime.Now
                }).ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(ChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved chat history: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves chat mode conversation history
    /// </summary>
    public static void SaveChatHistory(IEnumerable<(string role, string content)> context, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = context.Select(c => new ConversationMessage 
                { 
                    Role = c.role, 
                    Content = c.content,
                    Timestamp = DateTime.Now
                }).ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved chat history: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves voice chat mode conversation history
    /// </summary>
    public static async Task SaveVoiceChatHistoryAsync(IEnumerable<(string role, string content)> context, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = context.Select(c => new ConversationMessage 
                { 
                    Role = c.role, 
                    Content = c.content,
                    Timestamp = DateTime.Now
                }).ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(VoiceChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved voice chat history: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save voice chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves voice chat mode conversation history
    /// </summary>
    public static void SaveVoiceChatHistory(IEnumerable<(string role, string content)> context, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = context.Select(c => new ConversationMessage 
                { 
                    Role = c.role, 
                    Content = c.content,
                    Timestamp = DateTime.Now
                }).ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(VoiceChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved voice chat history: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save voice chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves voice chat mode conversation history with speaker metadata.
    /// Requirement 4.1: Include speaker identification metadata with each conversation turn.
    /// </summary>
    public static async Task SaveVoiceChatHistoryWithSpeakersAsync(IEnumerable<ConversationMessage> messages, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = messages.ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(VoiceChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved voice chat history with speaker metadata: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save voice chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Saves voice chat mode conversation history with speaker metadata.
    /// Requirement 4.1: Include speaker identification metadata with each conversation turn.
    /// </summary>
    public static void SaveVoiceChatHistoryWithSpeakers(IEnumerable<ConversationMessage> messages, string displayText)
    {
        try
        {
            var history = new ConversationHistory
            {
                Messages = messages.ToList(),
                DisplayText = displayText,
                LastUpdated = DateTime.Now
            };
            
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(VoiceChatHistoryPath, json);
            DevLog.WriteLine($"[History] Saved voice chat history with speaker metadata: {history.Messages.Count} messages");
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to save voice chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Loads chat mode conversation history
    /// </summary>
    public static async Task<ConversationHistory?> LoadChatHistoryAsync()
    {
        try
        {
            if (!File.Exists(ChatHistoryPath))
                return null;
                
            var json = await File.ReadAllTextAsync(ChatHistoryPath);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            
            if (history != null)
            {
                DevLog.WriteLine($"[History] Loaded chat history: {history.Messages.Count} messages from {history.LastUpdated}");
            }
            
            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to load chat history: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Loads chat mode conversation history
    /// </summary>
    public static ConversationHistory? LoadChatHistory()
    {
        try
        {
            if (!File.Exists(ChatHistoryPath))
                return null;
                
            var json = File.ReadAllText(ChatHistoryPath);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            
            if (history != null)
            {
                DevLog.WriteLine($"[History] Loaded chat history: {history.Messages.Count} messages from {history.LastUpdated}");
            }
            
            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to load chat history: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Loads voice chat mode conversation history
    /// </summary>
    public static async Task<ConversationHistory?> LoadVoiceChatHistoryAsync()
    {
        try
        {
            if (!File.Exists(VoiceChatHistoryPath))
                return null;
                
            var json = await File.ReadAllTextAsync(VoiceChatHistoryPath);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            
            if (history != null)
            {
                DevLog.WriteLine($"[History] Loaded voice chat history: {history.Messages.Count} messages from {history.LastUpdated}");
            }
            
            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to load voice chat history: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Loads voice chat mode conversation history
    /// </summary>
    public static ConversationHistory? LoadVoiceChatHistory()
    {
        try
        {
            if (!File.Exists(VoiceChatHistoryPath))
                return null;
                
            var json = File.ReadAllText(VoiceChatHistoryPath);
            var history = JsonSerializer.Deserialize<ConversationHistory>(json);
            
            if (history != null)
            {
                DevLog.WriteLine($"[History] Loaded voice chat history: {history.Messages.Count} messages from {history.LastUpdated}");
            }
            
            return history;
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to load voice chat history: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Clears chat mode history
    /// </summary>
    public static void ClearChatHistory()
    {
        try
        {
            if (File.Exists(ChatHistoryPath))
            {
                File.Delete(ChatHistoryPath);
                DevLog.WriteLine("[History] Cleared chat history");
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to clear chat history: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Clears voice chat mode history
    /// </summary>
    public static void ClearVoiceChatHistory()
    {
        try
        {
            if (File.Exists(VoiceChatHistoryPath))
            {
                File.Delete(VoiceChatHistoryPath);
                DevLog.WriteLine("[History] Cleared voice chat history");
            }
        }
        catch (Exception ex)
        {
            DevLog.WriteLine($"[History] Failed to clear voice chat history: {ex.Message}");
        }
    }
}
