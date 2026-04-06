using System;
using System.Threading;
using System.Threading.Tasks;

namespace TsukiAI.Core.Extensions;

/// <summary>
/// Extension methods for Task operations.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Executes a task with a timeout.
    /// </summary>
    /// <typeparam name="T">The return type of the task.</typeparam>
    /// <param name="task">The task to execute.</param>
    /// <param name="timeout">The timeout duration.</param>
    /// <param name="operationName">The name of the operation for error messages.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The result of the task.</returns>
    /// <exception cref="TimeoutException">Thrown when the operation times out.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task<T> WithTimeout<T>(
        this Task<T> task,
        TimeSpan timeout,
        string operationName,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        
        try
        {
            return await task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{operationName} timed out after {timeout.TotalSeconds} seconds. " +
                $"Try a shorter prompt or increase timeout in settings.");
        }
    }
    
    /// <summary>
    /// Executes a task with a timeout (non-generic version).
    /// </summary>
    /// <param name="task">The task to execute.</param>
    /// <param name="timeout">The timeout duration.</param>
    /// <param name="operationName">The name of the operation for error messages.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <exception cref="TimeoutException">Thrown when the operation times out.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task WithTimeout(
        this Task task,
        TimeSpan timeout,
        string operationName,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        
        try
        {
            await task.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{operationName} timed out after {timeout.TotalSeconds} seconds. " +
                $"Try a shorter prompt or increase timeout in settings.");
        }
    }
}
