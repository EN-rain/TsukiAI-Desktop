using System;

namespace TsukiAI.Core.Services;

/// <summary>
/// Base exception for inference-related errors.
/// </summary>
/// <remarks>
/// This is the base class for all inference-related exceptions in the TsukiAI system.
/// It provides a common exception type that can be caught to handle any inference error.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When a general inference error occurs that doesn't fit into more specific exception types</item>
///   <item>As a base class for more specific inference exceptions</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Check the exception message for specific error details</item>
///   <item>Review inference client configuration and settings</item>
///   <item>Verify model files and paths are correct</item>
///   <item>Check network connectivity if using remote inference</item>
///   <item>Consult application logs for additional context</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var result = await inferenceClient.ChatWithEmotionAsync(prompt, ct);
/// }
/// catch (InferenceException ex)
/// {
///     // Handle any inference-related error
///     DevLog.WriteLine($"Inference failed: {ex.Message}");
///     ShowErrorToUser("Unable to process request. Please try again.");
/// }
/// </code>
/// </remarks>
public class InferenceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    public InferenceException(string message) : base(message) { }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The error message that explains the reason for the exception.</param>
    /// <param name="inner">The exception that is the cause of the current exception, or null if no inner exception is specified.</param>
    public InferenceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Exception thrown when an inference operation exceeds its configured timeout duration.
/// </summary>
/// <remarks>
/// This exception indicates that an inference operation took longer than the allowed timeout period.
/// Timeouts help prevent the application from appearing frozen during network issues or slow model responses.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When an inference request exceeds the configured InferenceTimeout (default: 60 seconds)</item>
///   <item>When model loading exceeds the ModelLoadTimeout (default: 120 seconds)</item>
///   <item>When a health check exceeds the HealthCheckTimeout (default: 10 seconds)</item>
///   <item>During streaming operations that stall without producing tokens</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Try submitting a shorter or simpler prompt</item>
///   <item>Increase the timeout duration in Settings → Inference Timeout</item>
///   <item>Check network connectivity if using remote inference</item>
///   <item>Verify the inference server (Ollama/Colab) is responding normally</item>
///   <item>Consider switching to a faster model or inference mode</item>
///   <item>Check system resources (CPU/GPU/Memory) if using local inference</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var config = settings.GetTimeoutConfiguration();
///     var result = await inferenceClient
///         .ChatWithEmotionAsync(prompt, ct)
///         .WithTimeout(config.InferenceTimeout, "Inference", ct);
/// }
/// catch (InferenceTimeoutException ex)
/// {
///     DevLog.WriteLine($"Operation timed out after {ex.Timeout.TotalSeconds}s");
///     ShowErrorToUser($"Request timed out. {ex.Message}");
/// }
/// </code>
/// </remarks>
public sealed class InferenceTimeoutException : InferenceException
{
    /// <summary>
    /// Gets the timeout duration that was exceeded.
    /// </summary>
    /// <value>The <see cref="TimeSpan"/> representing the timeout threshold that was exceeded.</value>
    public TimeSpan Timeout { get; }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceTimeoutException"/> class with the specified timeout and operation name.
    /// </summary>
    /// <param name="timeout">The timeout duration that was exceeded.</param>
    /// <param name="operation">The name of the operation that timed out (e.g., "Inference", "Model loading", "Health check").</param>
    public InferenceTimeoutException(TimeSpan timeout, string operation)
        : base($"{operation} timed out after {timeout.TotalSeconds} seconds. " +
               $"Try a shorter prompt or increase timeout in settings.")
    {
        Timeout = timeout;
    }
}

/// <summary>
/// Exception thrown for network-related inference errors.
/// </summary>
/// <remarks>
/// This exception indicates that a network communication failure occurred during an inference operation.
/// Network errors are typically transient and may succeed on retry.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When HTTP requests to remote inference servers fail</item>
///   <item>When connection to Ollama server cannot be established</item>
///   <item>When network connectivity is lost during streaming responses</item>
///   <item>When DNS resolution fails for remote endpoints</item>
///   <item>When SSL/TLS handshake fails</item>
///   <item>When socket connections are refused or reset</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Check your internet connection and network status</item>
///   <item>Verify firewall settings allow connections to inference servers</item>
///   <item>Confirm the remote server URL is correct in settings</item>
///   <item>Check if proxy settings are required and configured</item>
///   <item>Wait a moment and retry - network errors are often transient</item>
///   <item>Enable automatic retries in Settings → Enable Inference Retries</item>
///   <item>Switch to local inference mode if remote server is unavailable</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var result = await remoteClient.ChatWithEmotionAsync(prompt, ct);
/// }
/// catch (InferenceNetworkException ex)
/// {
///     DevLog.WriteLine($"Network error: {ex.Message}");
///     
///     // Retry with exponential backoff
///     if (settings.EnableInferenceRetries)
///     {
///         await retryPolicy.ExecuteAsync(
///             ct => remoteClient.ChatWithEmotionAsync(prompt, ct),
///             RetryPolicy.IsRetryableException);
///     }
/// }
/// </code>
/// </remarks>
public sealed class InferenceNetworkException : InferenceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceNetworkException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">A description of the network error that occurred.</param>
    public InferenceNetworkException(string message)
        : base($"Network error: {message}. Check your internet connection and try again.") { }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceNetworkException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">A description of the network error that occurred.</param>
    /// <param name="inner">The underlying network exception (e.g., <see cref="HttpRequestException"/>, <see cref="System.Net.Sockets.SocketException"/>).</param>
    public InferenceNetworkException(string message, Exception inner)
        : base($"Network error: {message}. Check your internet connection and try again.", inner) { }
}

/// <summary>
/// Exception thrown for model-related errors during inference operations.
/// </summary>
/// <remarks>
/// This exception indicates that an error occurred related to the AI model itself, such as
/// loading failures, invalid model files, or model execution errors.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When a model file cannot be found at the specified path</item>
///   <item>When a model file is corrupted or has an invalid format</item>
///   <item>When model loading fails due to insufficient memory</item>
///   <item>When the model architecture is incompatible with the inference engine</item>
///   <item>When model quantization format is not supported</item>
///   <item>When GPU acceleration fails and CPU fallback is unavailable</item>
///   <item>When model context size is exceeded</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Verify the model is available in Ollama (run <c>ollama list</c>)</item>
///   <item>Check that Ollama is running and accessible</item>
///   <item>Ensure sufficient RAM/VRAM is available for the model size</item>
///   <item>Try a smaller or quantized version of the model</item>
///   <item>Re-download the model file if it may be corrupted</item>
///   <item>Check model compatibility with your inference engine version</item>
///   <item>Review model loading logs for specific error details</item>
///   <item>Switch to a different inference mode if model issues persist</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var client = new OllamaClient(settings.ModelName);
///     await client.WarmupModelAsync(ct);
/// }
/// catch (InferenceModelException ex)
/// {
///     DevLog.WriteLine($"Model error: {ex.Message}");
///     ShowErrorToUser(
///         "Failed to load model. Please check that Ollama is running and the model is available.",
///         "Model Loading Error");
/// }
/// </code>
/// </remarks>
public sealed class InferenceModelException : InferenceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceModelException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">A description of the model-related error that occurred.</param>
    public InferenceModelException(string message)
        : base($"Model error: {message}. Check model path and ensure file exists.") { }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceModelException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">A description of the model-related error that occurred.</param>
    /// <param name="inner">The underlying exception that caused the model error (e.g., <see cref="FileNotFoundException"/>, <see cref="OutOfMemoryException"/>).</param>
    public InferenceModelException(string message, Exception inner)
        : base($"Model error: {message}. Check model path and ensure file exists.", inner) { }
}

/// <summary>
/// Exception thrown for API key or authentication errors during inference operations.
/// </summary>
/// <remarks>
/// This exception indicates that authentication failed when attempting to access a remote inference service.
/// Authentication errors typically require user intervention to correct API key or credential configuration.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When an API key is missing or empty for remote inference</item>
///   <item>When an API key is invalid or has been revoked</item>
///   <item>When API key permissions are insufficient for the requested operation</item>
///   <item>When authentication tokens have expired</item>
///   <item>When HTTP 401 (Unauthorized) or 403 (Forbidden) responses are received</item>
///   <item>When API rate limits or quotas have been exceeded</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Verify your API key is correctly entered in Settings → Remote Inference API Key</item>
///   <item>Check that the API key has not expired or been revoked</item>
///   <item>Confirm the API key has appropriate permissions for inference operations</item>
///   <item>Check if you've exceeded API rate limits or usage quotas</item>
///   <item>Generate a new API key from your service provider if needed</item>
///   <item>Ensure no extra spaces or characters in the API key configuration</item>
///   <item>Switch to local inference mode if remote authentication cannot be resolved</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var client = new RemoteInferenceClient(
///         settings.RemoteInferenceUrl,
///         settings.RemoteInferenceApiKey,
///         settings.ModelName);
///     var result = await client.ChatWithEmotionAsync(prompt, ct);
/// }
/// catch (InferenceAuthenticationException ex)
/// {
///     DevLog.WriteLine($"Authentication failed: {ex.Message}");
///     ShowErrorToUser(
///         "Invalid API key. Please check your settings and try again.",
///         "Authentication Error");
///     OpenSettingsWindow();
/// }
/// </code>
/// </remarks>
public sealed class InferenceAuthenticationException : InferenceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceAuthenticationException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">A description of the authentication error that occurred.</param>
    public InferenceAuthenticationException(string message)
        : base($"Authentication error: {message}. Check your API key in settings.") { }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceAuthenticationException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">A description of the authentication error that occurred.</param>
    /// <param name="inner">The underlying exception that caused the authentication error (e.g., <see cref="HttpRequestException"/> with 401/403 status).</param>
    public InferenceAuthenticationException(string message, Exception inner)
        : base($"Authentication error: {message}. Check your API key in settings.", inner) { }
}

/// <summary>
/// Exception thrown when the inference server is unreachable or not responding.
/// </summary>
/// <remarks>
/// This exception indicates that the application cannot establish communication with the inference server.
/// This typically means the server is not running, not accessible, or experiencing issues.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When Ollama server is not running on the expected port (default: 11434)</item>
///   <item>When remote Colab server is not accessible at the configured URL</item>
///   <item>When health check requests to the server fail or timeout</item>
///   <item>When the server process has crashed or been terminated</item>
///   <item>When firewall rules block access to the server</item>
///   <item>When the server is running but not responding to requests</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>Ensure the inference server (Ollama/Colab) is running and started</item>
///   <item>For Ollama: Start the Ollama service using 'ollama serve' command</item>
///   <item>For Colab: Verify your Colab notebook is running and the ngrok tunnel is active</item>
///   <item>Check that the server URL and port are correctly configured in settings</item>
///   <item>Verify firewall settings allow connections to the server</item>
///   <item>Test server connectivity using curl or browser before retrying</item>
///   <item>Check server logs for errors or resource exhaustion</item>
///   <item>Restart the server if it appears to be hung or unresponsive</item>
///   <item>Switch to local Ollama inference if server issues persist</item>
/// </list>
/// 
/// <para><strong>Example:</strong></para>
/// <code>
/// try
/// {
///     var isReachable = await ollamaClient.IsServerReachableAsync(ct);
///     if (!isReachable)
///     {
///         throw new InferenceServerUnreachableException("Ollama");
///     }
/// }
/// catch (InferenceServerUnreachableException ex)
/// {
///     DevLog.WriteLine($"Server unreachable: {ex.Message}");
///     ShowErrorToUser(
///         "Ollama server is not responding. Please start Ollama and try again.",
///         "Server Unreachable");
/// }
/// </code>
/// </remarks>
public sealed class InferenceServerUnreachableException : InferenceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceServerUnreachableException"/> class with the specified server type.
    /// </summary>
    /// <param name="serverType">The type of server that is unreachable (e.g., "Ollama", "Colab", "Remote").</param>
    public InferenceServerUnreachableException(string serverType)
        : base($"{serverType} server not responding. Ensure {serverType} is running and accessible.") { }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceServerUnreachableException"/> class with the specified server type
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="serverType">The type of server that is unreachable (e.g., "Ollama", "Colab", "Remote").</param>
    /// <param name="inner">The underlying exception that caused the server to be unreachable (e.g., <see cref="HttpRequestException"/>, <see cref="TimeoutException"/>).</param>
    public InferenceServerUnreachableException(string serverType, Exception inner)
        : base($"{serverType} server not responding. Ensure {serverType} is running and accessible.", inner) { }
}

/// <summary>
/// Exception thrown when an API rate limit (429 Too Many Requests) is encountered.
/// </summary>
/// <remarks>
/// This exception indicates that the inference provider has rate-limited the requests.
/// When using multiple providers, this triggers automatic switching to the next available provider.
/// 
/// <para><strong>When Thrown:</strong></para>
/// <list type="bullet">
///   <item>When HTTP 429 (Too Many Requests) response is received from the API</item>
///   <item>When free tier rate limits are exceeded</item>
///   <item>When request quota for the current time window is exhausted</item>
/// </list>
/// 
/// <para><strong>Recovery Actions:</strong></para>
/// <list type="bullet">
///   <item>System automatically switches to next provider if multi-provider mode is enabled</item>
///   <item>Wait for rate limit window to reset (typically 1 minute to 1 hour)</item>
///   <item>Upgrade to paid tier for higher rate limits</item>
///   <item>Enable multi-provider mode to distribute load across multiple APIs</item>
/// </list>
/// </remarks>
public sealed class InferenceRateLimitException : InferenceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InferenceRateLimitException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">A description of the rate limit error that occurred.</param>
    public InferenceRateLimitException(string message)
        : base($"Rate limit exceeded: {message}") { }
}
