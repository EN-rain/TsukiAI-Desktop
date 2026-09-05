namespace TsukiAI.Core.Models;

/// <summary>
/// Configuration for inference operation timeouts and retry behavior.
/// </summary>
/// <remarks>
/// This configuration controls timeout durations and retry policies for all inference operations
/// in the TsukiAI system. It helps prevent the application from appearing frozen during network
/// issues or slow model responses, and provides automatic recovery from transient failures.
/// 
/// <para><strong>Usage:</strong></para>
/// <code>
/// var config = settings.GetTimeoutConfiguration();
/// var result = await inferenceClient
///     .ChatWithEmotionAsync(prompt, ct)
///     .WithTimeout(config.InferenceTimeout, "Inference", ct);
/// </code>
/// 
/// <para><strong>Configuration Sources:</strong></para>
/// <list type="bullet">
///   <item>Created from <see cref="AppSettings.GetTimeoutConfiguration()"/> method</item>
///   <item>Values are persisted in application settings</item>
///   <item>Can be customized per-user in Settings window</item>
/// </list>
/// 
/// <para><strong>Validation:</strong></para>
/// Use the <see cref="IsValid"/> method to ensure all values are within acceptable ranges
/// before applying the configuration.
/// </remarks>
public sealed record TimeoutConfiguration
{
    /// <summary>
    /// Gets or initializes the timeout duration for inference operations.
    /// </summary>
    /// <value>
    /// The timeout duration for inference requests. Default is 60 seconds.
    /// Valid range: 10-300 seconds.
    /// </value>
    /// <remarks>
    /// This timeout applies to:
    /// <list type="bullet">
    ///   <item>Chat inference requests (streaming and non-streaming)</item>
    ///   <item>Conversation summarization operations</item>
    ///   <item>Any other inference API calls</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Use 30-60 seconds for fast local models</item>
    ///   <item>Use 60-120 seconds for remote inference or slower models</item>
    ///   <item>Increase timeout if you frequently see timeout errors</item>
    ///   <item>Decrease timeout for faster failure detection</item>
    /// </list>
    /// </remarks>
    public TimeSpan InferenceTimeout { get; init; } = TimeSpan.FromSeconds(60);
    
    /// <summary>
    /// Gets or initializes the timeout duration for model loading operations.
    /// </summary>
    /// <value>
    /// The timeout duration for loading and warming up models. Default is 120 seconds.
    /// Valid range: 30-600 seconds.
    /// </value>
    /// <remarks>
    /// This timeout applies to:
    /// <list type="bullet">
    ///   <item>Loading models from Ollama</item>
    ///   <item>Pulling models from Ollama registry</item>
    ///   <item>Initializing remote inference connections</item>
    ///   <item>Model warmup operations</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Use 60-120 seconds for small models (1-3GB)</item>
    ///   <item>Use 120-300 seconds for medium models (3-7GB)</item>
    ///   <item>Use 300-600 seconds for large models (7GB+)</item>
    ///   <item>Consider SSD speed and available RAM when setting this value</item>
    /// </list>
    /// </remarks>
    public TimeSpan ModelLoadTimeout { get; init; } = TimeSpan.FromSeconds(120);
    
    /// <summary>
    /// Gets or initializes the timeout duration for health check operations.
    /// </summary>
    /// <value>
    /// The timeout duration for server health checks. Default is 10 seconds.
    /// Valid range: 5-60 seconds.
    /// </value>
    /// <remarks>
    /// This timeout applies to:
    /// <list type="bullet">
    ///   <item>Checking if Ollama server is reachable</item>
    ///   <item>Verifying remote inference endpoints are responding</item>
    ///   <item>Pre-flight checks before mode switching</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Use 5-10 seconds for local servers (Ollama)</item>
    ///   <item>Use 10-30 seconds for remote servers with good connectivity</item>
    ///   <item>Use 30-60 seconds for remote servers with poor connectivity</item>
    /// </list>
    /// </remarks>
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(10);
    
    /// <summary>
    /// Gets or initializes the maximum number of retry attempts for failed operations.
    /// </summary>
    /// <value>
    /// The maximum number of times to retry a failed operation. Default is 3.
    /// Valid range: 0-10 retries.
    /// </value>
    /// <remarks>
    /// Retry logic uses exponential backoff with delays calculated from <see cref="InitialRetryDelay"/>.
    /// For example, with default settings:
    /// <list type="bullet">
    ///   <item>Attempt 1: Immediate</item>
    ///   <item>Attempt 2: After 1 second</item>
    ///   <item>Attempt 3: After 2 seconds</item>
    ///   <item>Attempt 4: After 4 seconds</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Use 2-3 retries for stable networks</item>
    ///   <item>Use 3-5 retries for unreliable networks</item>
    ///   <item>Use 0 retries to disable automatic retry (fail fast)</item>
    ///   <item>Higher values increase total wait time before final failure</item>
    /// </list>
    /// 
    /// <para><strong>Note:</strong></para>
    /// Only transient errors (network issues, timeouts) are retried.
    /// Permanent errors (invalid API key, model not found) fail immediately.
    /// </remarks>
    public int MaxRetries { get; init; } = 3;
    
    /// <summary>
    /// Gets or initializes the initial delay before the first retry attempt.
    /// </summary>
    /// <value>
    /// The initial retry delay. Default is 1 second.
    /// Valid range: 0.5-10 seconds.
    /// </value>
    /// <remarks>
    /// Subsequent retries use exponential backoff, doubling the delay each time:
    /// <list type="bullet">
    ///   <item>Retry 1: InitialRetryDelay (e.g., 1s)</item>
    ///   <item>Retry 2: InitialRetryDelay × 2 (e.g., 2s)</item>
    ///   <item>Retry 3: InitialRetryDelay × 4 (e.g., 4s)</item>
    ///   <item>Retry 4: InitialRetryDelay × 8 (e.g., 8s)</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Use 0.5-1 second for fast recovery from transient errors</item>
    ///   <item>Use 2-5 seconds to reduce server load during outages</item>
    ///   <item>Lower values provide faster recovery but may overwhelm servers</item>
    ///   <item>Higher values are more polite but increase total wait time</item>
    /// </list>
    /// </remarks>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    
    /// <summary>
    /// Gets or initializes whether automatic retries are enabled for transient failures.
    /// </summary>
    /// <value>
    /// <c>true</c> to enable automatic retries; <c>false</c> to fail immediately. Default is <c>true</c>.
    /// </value>
    /// <remarks>
    /// When enabled, operations that fail with retryable errors (network issues, timeouts)
    /// will be automatically retried up to <see cref="MaxRetries"/> times with exponential backoff.
    /// 
    /// <para><strong>Retryable Errors:</strong></para>
    /// <list type="bullet">
    ///   <item><see cref="System.Net.Http.HttpRequestException"/> - Network connectivity issues</item>
    ///   <item><see cref="TaskCanceledException"/> - Request timeouts</item>
    ///   <item><see cref="TimeoutException"/> - Operation timeouts</item>
    ///   <item><see cref="System.IO.IOException"/> - I/O errors</item>
    /// </list>
    /// 
    /// <para><strong>Non-Retryable Errors:</strong></para>
    /// <list type="bullet">
    ///   <item>Invalid API keys or authentication failures</item>
    ///   <item>Model not found or invalid model files</item>
    ///   <item>Invalid request parameters</item>
    ///   <item>User-initiated cancellations</item>
    /// </list>
    /// 
    /// <para><strong>Recommendations:</strong></para>
    /// <list type="bullet">
    ///   <item>Enable for production use to handle transient network issues</item>
    ///   <item>Disable for debugging to see failures immediately</item>
    ///   <item>Disable if you prefer manual retry control</item>
    /// </list>
    /// </remarks>
    public bool EnableRetries { get; init; } = true;
    
    /// <summary>
    /// Validates that all configuration values are within acceptable ranges.
    /// </summary>
    /// <returns>
    /// <c>true</c> if all configuration values are valid; otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method checks that:
    /// <list type="bullet">
    ///   <item><see cref="InferenceTimeout"/> is between 10 and 300 seconds</item>
    ///   <item><see cref="ModelLoadTimeout"/> is between 30 and 600 seconds</item>
    ///   <item><see cref="HealthCheckTimeout"/> is between 5 and 60 seconds</item>
    ///   <item><see cref="MaxRetries"/> is between 0 and 10</item>
    ///   <item><see cref="InitialRetryDelay"/> is between 0.5 and 10 seconds</item>
    /// </list>
    /// 
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// var config = new TimeoutConfiguration
    /// {
    ///     InferenceTimeout = TimeSpan.FromSeconds(90),
    ///     MaxRetries = 5
    /// };
    /// 
    /// if (!config.IsValid())
    /// {
    ///     throw new InvalidOperationException("Invalid timeout configuration");
    /// }
    /// </code>
    /// </remarks>
    public bool IsValid()
    {
        return InferenceTimeout.TotalSeconds >= 10 && InferenceTimeout.TotalSeconds <= 300
            && ModelLoadTimeout.TotalSeconds >= 30 && ModelLoadTimeout.TotalSeconds <= 600
            && HealthCheckTimeout.TotalSeconds >= 5 && HealthCheckTimeout.TotalSeconds <= 60
            && MaxRetries >= 0 && MaxRetries <= 10
            && InitialRetryDelay.TotalSeconds >= 0.5 && InitialRetryDelay.TotalSeconds <= 10;
    }
}
