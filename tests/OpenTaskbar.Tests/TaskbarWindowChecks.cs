using OpenTaskbar.Services;

internal static class TaskbarWindowChecks
{
    private const long AppWindow = TaskbarWindowRules.AppWindow;
    private const long ToolWindow = TaskbarWindowRules.ToolWindow;
    private const long NoActivate = TaskbarWindowRules.NoActivate;

    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        Check(TaskbarWindowRules.HasTaskbarButton(0, false, false),
            "A normal visible window must keep its taskbar button.");
        Check(!TaskbarWindowRules.HasTaskbarButton(ToolWindow, false, false),
            "Tool windows are background helpers and must not appear as normal app buttons.");
        Check(TaskbarWindowRules.HasTaskbarButton(ToolWindow | AppWindow, false, false),
            "A tool window that explicitly opts in with WS_EX_APPWINDOW must still appear.");
        Check(!TaskbarWindowRules.HasTaskbarButton(NoActivate, false, false),
            "Non-activatable overlay and helper windows must not appear as normal app buttons.");
        Check(!TaskbarWindowRules.HasTaskbarButton(ToolWindow | NoActivate, false, false),
            "Helper windows need the explicit opt-in even when they carry both helper styles.");
        Check(TaskbarWindowRules.HasTaskbarButton(NoActivate | AppWindow, false, false),
            "A non-activatable window with WS_EX_APPWINDOW must still appear.");
        Check(!TaskbarWindowRules.HasTaskbarButton(0, true, false),
            "An owned window has no taskbar button unless it opts in.");
        Check(TaskbarWindowRules.HasTaskbarButton(AppWindow, true, false),
            "An owned window with WS_EX_APPWINDOW follows the shell rule and must appear.");
        Check(!TaskbarWindowRules.HasTaskbarButton(0, false, true),
            "A window removed with ITaskbarList::DeleteTab must stay hidden even when otherwise eligible.");
        Check(!TaskbarWindowRules.HasTaskbarButton(AppWindow, true, true),
            "ITaskbarList::DeleteTab must override both WS_EX_APPWINDOW and the owner opt-in.");

        Check(TaskbarWindowRules.IsHiddenCloak(1) && TaskbarWindowRules.IsHiddenCloak(4),
            "App- and inherited-cloaked windows are hidden from the taskbar.");
        Check(!TaskbarWindowRules.IsHiddenCloak(0) && !TaskbarWindowRules.IsHiddenCloak(TaskbarWindowRules.CloakedShell),
            "Visible windows and windows cloaked by the shell for another virtual desktop are kept.");

        Check(TaskbarWindowRules.IsHiddenImmersiveFrame("Windows.UI.Core.CoreWindow", 0),
            "A core window without WS_EX_WINDOWEDGE is an internal shell surface.");
        Check(TaskbarWindowRules.IsHiddenImmersiveFrame("ApplicationFrameWindow", 0),
            "A hidden UWP frame is not an app button.");
        Check(!TaskbarWindowRules.IsHiddenImmersiveFrame("ApplicationFrameWindow", TaskbarWindowRules.WindowEdge),
            "A real Store app frame carries WS_EX_WINDOWEDGE and must be tracked.");
        Check(!TaskbarWindowRules.IsHiddenImmersiveFrame("MozillaWindowClass", 0),
            "Normal application classes must never be treated as immersive frames.");

        // Issue #152: Native taskbar class matching for EVENT_OBJECT_SHOW re-hiding.
        Check(TaskbarReplacementSession.IsPrimaryTaskbarClassName("Shell_TrayWnd")
            && TaskbarReplacementSession.IsPrimaryTaskbarClassName("shell_traywnd")
            && TaskbarReplacementSession.IsNativeTaskbarClassName("Shell_TrayWnd")
            && !TaskbarReplacementSession.IsSecondaryTaskbarClassName("Shell_TrayWnd"),
            "Shell_TrayWnd must match primary and native taskbar checks case-insensitively.");
        Check(TaskbarReplacementSession.IsSecondaryTaskbarClassName("Shell_SecondaryTrayWnd")
            && TaskbarReplacementSession.IsNativeTaskbarClassName("Shell_SecondaryTrayWnd")
            && !TaskbarReplacementSession.IsPrimaryTaskbarClassName("Shell_SecondaryTrayWnd"),
            "Shell_SecondaryTrayWnd must match secondary and native taskbar checks.");
        Check(!TaskbarReplacementSession.IsNativeTaskbarClassName("Progman")
            && !TaskbarReplacementSession.IsNativeTaskbarClassName("WorkerW")
            && !TaskbarReplacementSession.IsNativeTaskbarClassName("Chrome_WidgetWin_1")
            && !TaskbarReplacementSession.IsNativeTaskbarClassName(""),
            "Unrelated window classes must never be treated as native taskbars.");

        // Issue #152: Cold-boot Degraded recovery and Explorer restart re-registration detection.
        Check(TaskbarReplacementSession.ShouldReRegisterTaskbar(
                isAppBarRegistered: false, registeredTaskbarHandle: 0, registeredTaskbarPid: 0,
                currentTaskbarHandle: 0x1000, currentTaskbarPid: 4200),
            "An unregistered session (e.g. started Degraded before Explorer existed) must register AppBar on recovery.");
        Check(!TaskbarReplacementSession.ShouldReRegisterTaskbar(
                isAppBarRegistered: true, registeredTaskbarHandle: 0x1000, registeredTaskbarPid: 4200,
                currentTaskbarHandle: 0x1000, currentTaskbarPid: 4200),
            "Same Explorer Shell_TrayWnd HWND and PID must not trigger duplicate ABM_NEW registration.");
        Check(TaskbarReplacementSession.ShouldReRegisterTaskbar(
                isAppBarRegistered: true, registeredTaskbarHandle: 0x1000, registeredTaskbarPid: 4200,
                currentTaskbarHandle: 0x2000, currentTaskbarPid: 4200),
            "A recreated Shell_TrayWnd HWND must trigger AppBar re-registration even if TaskbarCreated was missed.");
        Check(TaskbarReplacementSession.ShouldReRegisterTaskbar(
                isAppBarRegistered: true, registeredTaskbarHandle: 0x1000, registeredTaskbarPid: 4200,
                currentTaskbarHandle: 0x1000, currentTaskbarPid: 9900),
            "A recycled Shell_TrayWnd handle owned by a new Explorer PID must trigger AppBar re-registration.");
        Check(!TaskbarReplacementSession.ShouldReRegisterTaskbar(
                isAppBarRegistered: true, registeredTaskbarHandle: 0, registeredTaskbarPid: 0,
                currentTaskbarHandle: 0x1000, currentTaskbarPid: 4200),
            "A registered session without a recorded taskbar handle must not spuriously duplicate ABM_NEW.");

        // Issue #152: Bounded, zero-idle-cost cold-boot / Degraded reconcile backoff (~60 s budget).
        var degradedTotalMs = 0;
        for (var attempt = 1; attempt <= TaskbarReplacementSession.MaxDegradedReconcileAttempts; attempt++)
        {
            var decision = TaskbarReplacementSession.EvaluateDegradedReconcileRetry(attempt);
            Check(decision.ShouldRetry && !decision.Exhausted && decision.DelayMs > 0,
                $"Degraded reconcile attempt {attempt} must schedule a positive retry delay.");
            degradedTotalMs += decision.DelayMs;
        }
        Check(degradedTotalMs == 60_000,
            $"Degraded reconcile budget must cover 60 s of cold-boot Explorer startup (got {degradedTotalMs} ms).");
        Check(TaskbarReplacementSession.EvaluateDegradedReconcileRetry(1).DelayMs == 500
            && TaskbarReplacementSession.EvaluateDegradedReconcileRetry(10).DelayMs == 500
            && TaskbarReplacementSession.EvaluateDegradedReconcileRetry(11).DelayMs == 1000
            && TaskbarReplacementSession.EvaluateDegradedReconcileRetry(25).DelayMs == 1000
            && TaskbarReplacementSession.EvaluateDegradedReconcileRetry(26).DelayMs == 2000
            && TaskbarReplacementSession.EvaluateDegradedReconcileRetry(45).DelayMs == 2000,
            "Degraded reconcile backoff must step 500 ms -> 1000 ms -> 2000 ms.");
        var degradedExhausted = TaskbarReplacementSession.EvaluateDegradedReconcileRetry(
            TaskbarReplacementSession.MaxDegradedReconcileAttempts + 1);
        Check(!degradedExhausted.ShouldRetry && degradedExhausted.Exhausted && degradedExhausted.DelayMs == 0,
            "Degraded reconcile must stop retrying after its bounded budget to guarantee zero idle cost.");
        Check(TaskbarReplacementSession.EvaluateDegradedReconcileRetry(0).DelayMs == 500,
            "Non-positive degraded attempt counts must clamp safely to the initial retry step.");

        // Issue #152: Startup settle and normal hide-retry step state machine.
        Check(TaskbarReplacementSession.HideRetryInitialDelayMs(requireStableSettle: true) == 500
            && TaskbarReplacementSession.HideRetryInitialDelayMs(requireStableSettle: false) == 250,
            "Startup settle must start at 500 ms and reactive re-hide at 250 ms.");

        var missingStep = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.MissingTaskbar, attemptNumber: 3, consecutiveClean: 4,
            requireStableSettle: true);
        Check(missingStep.StopTimer && missingStep.NeedsDegradedReconcile && !missingStep.Settled
            && !missingStep.Exhausted && missingStep.NextConsecutiveClean == 0,
            "Disappearing Shell_TrayWnd during hide retry must stop hide timer and hand off to Degraded reconcile.");

        // Startup settle must NOT stop on the first AlreadyHidden pass; it must require the full clean streak
        // and reset the streak whenever Explorer re-shows a taskbar mid-logon.
        var firstClean = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.AlreadyHidden, attemptNumber: 1, consecutiveClean: 0,
            requireStableSettle: true);
        Check(!firstClean.StopTimer && !firstClean.Settled && firstClean.NextConsecutiveClean == 1 && firstClean.NextDelayMs == 500,
            "A single hidden observation at startup must not stop logon settle enforcement prematurely.");

        var midLogonReshow = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.HidVisible, attemptNumber: 5, consecutiveClean: 4,
            requireStableSettle: true);
        Check(!midLogonReshow.StopTimer && !midLogonReshow.Settled && midLogonReshow.NextConsecutiveClean == 0,
            "Re-hiding a taskbar that Explorer showed mid-logon must reset the consecutive-clean streak.");

        var settledStartup = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.AlreadyHidden, attemptNumber: 15,
            consecutiveClean: TaskbarReplacementSession.StartupSettleRequiredCleanPasses - 1,
            requireStableSettle: true);
        Check(settledStartup.StopTimer && settledStartup.Settled && !settledStartup.Exhausted
            && settledStartup.NextConsecutiveClean == TaskbarReplacementSession.StartupSettleRequiredCleanPasses,
            "Startup settle must stop with zero idle cost once the required consecutive clean passes complete.");

        var normalFirstClean = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.AlreadyHidden, attemptNumber: 1, consecutiveClean: 0,
            requireStableSettle: false);
        var normalSecondClean = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.AlreadyHidden, attemptNumber: 2,
            consecutiveClean: normalFirstClean.NextConsecutiveClean, requireStableSettle: false);
        Check(!normalFirstClean.StopTimer && normalSecondClean.StopTimer && normalSecondClean.Settled && !normalSecondClean.Exhausted,
            "Normal reactive re-hide must settle and stop after two consecutive clean passes.");

        var normalExhausted = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.StillVisible,
            attemptNumber: TaskbarReplacementSession.NormalHideMaxAttempts, consecutiveClean: 0,
            requireStableSettle: false);
        Check(normalExhausted.StopTimer && !normalExhausted.Settled && normalExhausted.Exhausted,
            "Persistent StillVisible past the hide retry budget must stop and report exhaustion.");

        var finalAttemptHid = TaskbarReplacementSession.EvaluateHideRetryStep(
            TaskbarReplacementSession.HideObservation.HidVisible,
            attemptNumber: TaskbarReplacementSession.StartupSettleMaxAttempts, consecutiveClean: 0,
            requireStableSettle: true);
        Check(finalAttemptHid.StopTimer && finalAttemptHid.Settled && !finalAttemptHid.Exhausted,
            "Successfully hiding on the final bounded attempt must stop cleanly without reporting a false error.");

        return checks;
    }
}
