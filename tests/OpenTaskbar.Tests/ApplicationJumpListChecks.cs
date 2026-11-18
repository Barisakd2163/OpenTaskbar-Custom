using OpenTaskbar.Services;

internal static class ApplicationJumpListChecks
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }

        // A jump list task must be launched exactly like a normal dock app launch: shell-executed, with the
        // arguments the application published and its own working directory intact.
        const string executable = @"C:\Program Files\LibreWolf\librewolf.exe";
        var info = NativeJumpListSession.BuildLaunchInfo(executable, "-private-window", @"C:\Program Files\LibreWolf");
        Check(info.FileName == executable && info.Arguments == "-private-window"
            && info.WorkingDirectory == @"C:\Program Files\LibreWolf" && info.UseShellExecute,
            "A jump list task must preserve its target, arguments and working directory when launched directly.");

        var withoutDirectory = NativeJumpListSession.BuildLaunchInfo("notepad.exe", string.Empty, string.Empty);
        Check(withoutDirectory.Arguments.Length == 0 && withoutDirectory.WorkingDirectory.Length == 0
            && withoutDirectory.UseShellExecute,
            "An empty task argument or working directory must not be injected into the launch.");

        return checks;
    }
}
