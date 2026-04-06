namespace TsukiAI.Core.Services;

/// <summary>
/// Common interface for all inference client implementations.
/// Supports both streaming and non-streaming inference with emotion detection.
/// </summary>
public interface IInferenceClient : IDisposable
{
    /// <summary>
    /// Gets the model name or identifier.
    /// </summary>
    string Model { get; }
    
    /// <summary>
    /// Gets whether the model is loaded and ready for inference.
    /// </summary>
    bool IsLoaded { get; }
    
    /// <summary>
    /// Gets whether the model has been warmed up.
    /// </summary>
    bool IsWarmedUp { get; }
    
    /// <summary>
    /// Checks if the inference server/backend is reachable.
    /// </summary>
    /// <param name="ct">Cancellation token to cancel the health check operation. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>True if the server is reachable, false otherwise.</returns>
    Task<bool> IsServerReachableAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Warms up the model (loads into memory or verifies connectivity).
    /// </summary>
    /// <param name="model">Optional model name to warm up. If null, uses the current model.</param>
    /// <param name="ct">Cancellation token to cancel the warmup operation. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>True if warmup was successful, false otherwise.</returns>
    Task<bool> WarmupModelAsync(string? model = null, CancellationToken ct = default);
    
    /// <summary>
    /// Generates a chat response with emotion detection (non-streaming).
    /// </summary>
    /// <param name="userText">The user's input text.</param>
    /// <param name="personaName">Optional persona name for the assistant.</param>
    /// <param name="preferredEmotion">Optional preferred emotion for the response.</param>
    /// <param name="history">Optional conversation history for context.</param>
    /// <param name="ct">Cancellation token to cancel the inference operation. When cancelled, throws <see cref="OperationCanceledException"/>. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <param name="systemInstructions">Optional system instructions to override defaults.</param>
    /// <returns>An AI reply with text, emotion, and optional TTS text.</returns>
    Task<AiReply> ChatWithEmotionAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        CancellationToken ct = default,
        string? systemInstructions = null);
    
    /// <summary>
    /// Generates a chat response with emotion detection (streaming).
    /// </summary>
    /// <param name="userText">The user's input text.</param>
    /// <param name="personaName">Optional persona name for the assistant.</param>
    /// <param name="preferredEmotion">Optional preferred emotion for the response.</param>
    /// <param name="history">Optional conversation history for context.</param>
    /// <param name="onPartialReply">Optional callback invoked with partial response text as it streams.</param>
    /// <param name="ct">Cancellation token to cancel the streaming operation. When cancelled, stops streaming immediately and throws <see cref="OperationCanceledException"/>. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <param name="systemInstructions">Optional system instructions to override defaults.</param>
    /// <returns>An AI reply with text, emotion, and optional TTS text.</returns>
    Task<AiReply> ChatWithEmotionStreamingAsync(
        string userText,
        string? personaName = null,
        string? preferredEmotion = null,
        IReadOnlyList<(string role, string content)>? history = null,
        Action<string>? onPartialReply = null,
        CancellationToken ct = default,
        string? systemInstructions = null);
    
    /// <summary>
    /// Summarizes conversation history into a concise summary.
    /// </summary>
    /// <param name="history">The conversation history to summarize.</param>
    /// <param name="ct">Cancellation token to cancel the summarization operation. When cancelled, throws <see cref="OperationCanceledException"/>. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>A concise summary of the conversation.</returns>
    Task<string> SummarizeConversationAsync(
        IReadOnlyList<(string role, string content)> history,
        CancellationToken ct = default);
    
    /// <summary>
    /// Sets the model to use for inference.
    /// </summary>
    /// <param name="model">The model name or identifier.</param>
    void SetModel(string model);
}

/// <summary>
/// Represents an AI reply with emotion metadata.
/// </summary>
/// <param name="Reply">The text content of the reply.</param>
/// <param name="Emotion">The detected or assigned emotion for the reply.</param>
/// <param name="TtsText">Optional text optimized for text-to-speech synthesis.</param>
public sealed record AiReply(string Reply, string Emotion, string? TtsText = null);
