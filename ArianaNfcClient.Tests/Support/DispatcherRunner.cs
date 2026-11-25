using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ArianaNfcClient.Tests.Support;

/// <summary>
/// Runs an async test body on an STA thread with a WPF dispatcher, so that
/// <c>await Dispatcher.Yield()</c> in the view model can continue.
/// </summary>
internal static class DispatcherRunner
{
    public static void Run(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Dispatcher test body did not finish within 30 seconds.");
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
