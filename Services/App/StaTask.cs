using System.Collections.Concurrent;

namespace OpenTaskbar.Services;

/// <summary>
/// A single background STA thread for Shell/UI Automation work. Cross-process UIA tree walks and COM calls
/// must not run on the UI dispatcher: a single measurement can block input for hundreds of milliseconds
/// (issue #128 finding 1). One thread keeps the calls ordered and off the render/input path.
/// </summary>
internal static class StaTask
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var thread = new Thread(() =>
        {
            foreach (var item in Work.GetConsumingEnumerable()) item();
        })
        {
            IsBackground = true,
            Name = "OpenTaskbar STA worker",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    // Window enumeration runs on its own STA thread so a 50-128 ms scan can never make a Start-menu
    // measurement wait behind it (issue #128 findings 1 and 6).
    private static readonly BlockingCollection<Action> ScanWork = new();
    private static readonly Lazy<Thread> ScanWorker = new(() =>
    {
        var thread = new Thread(() =>
        {
            foreach (var item in ScanWork.GetConsumingEnumerable()) item();
        })
        {
            IsBackground = true,
            Name = "OpenTaskbar window scanner",
            Priority = ThreadPriority.BelowNormal
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    public static Task<T> Run<T>(Func<T> work) => Submit(Work, Worker, work);

    public static Task<T> RunScan<T>(Func<T> work) => Submit(ScanWork, ScanWorker, work);

    private static Task<T> Submit<T>(BlockingCollection<Action> queue, Lazy<Thread> worker, Func<T> work)
    {
        _ = worker.Value;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
}
