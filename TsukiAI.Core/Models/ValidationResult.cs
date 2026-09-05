namespace TsukiAI.Core.Models;

/// <summary>
/// Represents the result of a configuration validation operation.
/// </summary>
/// <remarks>
/// This record encapsulates the outcome of validating inference client configurations
/// before creating or switching to a new inference mode. It provides a simple success/failure
/// indication along with a descriptive error message when validation fails.
/// 
/// <para><strong>Usage:</strong></para>
/// <code>
/// var factory = new InferenceClientFactory();
/// var validation = factory.ValidateConfiguration(InferenceMode.LocalNative, settings);
/// 
/// if (!validation.IsValid)
/// {
///     ShowErrorToUser($"Configuration error: {validation.ErrorMessage}");
///     return;
/// }
/// 
/// // Proceed with client creation
/// var client = await factory.CreateClientAsync(InferenceMode.LocalNative, settings, ct);
/// </code>
/// 
/// <para><strong>Validation Checks:</strong></para>
/// Different inference modes have different validation requirements:
/// <list type="bullet">
///   <item><strong>LocalNative:</strong> Model file exists, directory is valid</item>
///   <item><strong>LocalOllama:</strong> Model name is configured</item>
///   <item><strong>RemoteColab:</strong> URL is valid, uses HTTP/HTTPS protocol</item>
/// </list>
/// 
/// <para><strong>Design Pattern:</strong></para>
/// This follows the Result pattern, providing a type-safe way to return success or failure
/// without throwing exceptions for expected validation failures.
/// </remarks>
public sealed record ValidationResult
{
    /// <summary>
    /// Gets whether the validation was successful.
    /// </summary>
    /// <value>
    /// <c>true</c> if the configuration is valid and can be used to create an inference client;
    /// <c>false</c> if validation failed and <see cref="ErrorMessage"/> contains details.
    /// </value>
    /// <remarks>
    /// Always check this property before proceeding with client creation or mode switching.
    /// 
    /// <para><strong>Example:</strong></para>
    /// <code>
    /// if (validation.IsValid)
    /// {
    ///     // Safe to proceed
    ///     var client = await factory.CreateClientAsync(mode, settings, ct);
    /// }
    /// else
    /// {
    ///     // Handle validation failure
    ///     DevLog.WriteLine($"Validation failed: {validation.ErrorMessage}");
    /// }
    /// </code>
    /// </remarks>
    public bool IsValid { get; init; }
    
    /// <summary>
    /// Gets the error message if validation failed, or null if validation succeeded.
    /// </summary>
    /// <value>
    /// A descriptive error message explaining why validation failed, or null if <see cref="IsValid"/> is true.
    /// </value>
    /// <remarks>
    /// Error messages are user-friendly and actionable, providing guidance on how to fix the issue.
    /// 
    /// <para><strong>Example Error Messages:</strong></para>
    /// <list type="bullet">
    ///   <item>"Model not found in Ollama. Please run 'ollama pull model-name' to download the model."</item>
    ///   <item>"Model name is required for Ollama mode. Please set the model name in settings."</item>
    ///   <item>"Remote inference URL is required. Please configure your Colab endpoint or API URL in settings."</item>
    ///   <item>"Invalid remote inference URL format. Please check the URL and try again."</item>
    /// </list>
    /// 
    /// <para><strong>Usage:</strong></para>
    /// <code>
    /// if (!validation.IsValid)
    /// {
    ///     MessageBox.Show(
    ///         validation.ErrorMessage,
    ///         "Configuration Error",
    ///         MessageBoxButton.OK,
    ///         MessageBoxImage.Warning);
    /// }
    /// </code>
    /// </remarks>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationResult"/> record.
    /// </summary>
    /// <param name="isValid">Whether the validation was successful.</param>
    /// <param name="errorMessage">The error message if validation failed, or null if successful.</param>
    /// <remarks>
    /// <para><strong>Creating Success Results:</strong></para>
    /// <code>
    /// return new ValidationResult(true);
    /// // or
    /// return new ValidationResult(true, null);
    /// </code>
    /// 
    /// <para><strong>Creating Failure Results:</strong></para>
    /// <code>
    /// return new ValidationResult(false, "Model file not found");
    /// </code>
    /// 
    /// <para><strong>Best Practices:</strong></para>
    /// <list type="bullet">
    ///   <item>Always provide a descriptive error message when IsValid is false</item>
    ///   <item>Include actionable guidance in error messages</item>
    ///   <item>Mention specific settings or paths that need correction</item>
    ///   <item>Keep error messages concise but informative</item>
    /// </list>
    /// </remarks>
    public ValidationResult(bool isValid, string? errorMessage = null)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }
}
