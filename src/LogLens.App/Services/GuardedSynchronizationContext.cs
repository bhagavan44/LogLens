using Microsoft.UI.Dispatching;

namespace LogLens.App.Services;

/// <summary>
/// The UI thread's synchronization context: like WinUI's <c>DispatcherQueueSynchronizationContext</c>, but an exception thrown by a
/// posted callback (an <c>async void</c> continuation, an awaited continuation that throws, a <c>TryEnqueue</c> lambda) goes through the
/// app's crash policy instead of ending the process without a trace. WinUI raises <c>Application.UnhandledException</c> for XAML event
/// handlers only; dispatcher callbacks fail fast.
/// </summary>
internal sealed class GuardedSynchronizationContext(DispatcherQueue queue, Func<Exception, string, bool> tryContinue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
        => queue.TryEnqueue(() => Run(d, state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (queue.HasThreadAccess)
        {
            Run(d, state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        queue.TryEnqueue(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null)
        {
            throw new InvalidOperationException("A callback sent to the UI thread failed.", error);
        }
    }

    public override SynchronizationContext CreateCopy() => new GuardedSynchronizationContext(queue, tryContinue);

    /// <summary>Runs a queued callback; if it throws and the policy says the app may carry on, the exception stops here.</summary>
    public void Run(SendOrPostCallback d, object? state)
    {
        try
        {
            d(state);
        }
        catch (Exception ex) when (tryContinue(ex, "UI callback"))
        {
            // reported to the user and logged by the policy
        }
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread with the same protection as posted callbacks.</summary>
    public static bool Enqueue(DispatcherQueue queue, Action action)
        => queue.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (App.TryContinueAfter(ex, "UI callback"))
            {
                // reported to the user and logged by the policy
            }
        });
}
