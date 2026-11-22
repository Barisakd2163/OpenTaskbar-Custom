using System.Runtime.ExceptionServices;

// WPF controls require an STA thread. The test runner's main thread is MTA, so new UI-only checks
// are executed on a dedicated STA thread and any failure is rethrown on the caller with its stack intact.
internal static class StaCheckRunner
{
    internal static int Run(Func<int> body)
    {
        Exception? failure = null;
        var checks = 0;
        var thread = new Thread(() =>
        {
            try
            {
                checks = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }
}
