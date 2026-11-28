using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

internal static class StartupRegistration
{
    internal const string LaunchArgument = "--startup";
    internal const string EnableArgument = "--enable-startup";
    internal const string DisableArgument = "--disable-startup";
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;
    private const int LogonTrigger = 9;
    private const int ExecutableAction = 0;
    private const int IgnoreNewInstance = 2;
    private const int TaskMissing = unchecked((int)0x80070002);
    private const int TaskPathMissing = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    internal static bool IsEnabled()
    {
        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
        dynamic scheduler = Connect();
        dynamic root = scheduler.GetFolder("\\");
        dynamic task = FindTask(root, TaskName(sid));
        if (task == null || !task.Enabled) return false;
        dynamic actions = task.Definition.Actions;
        if (actions.Count != 1) return false;
        dynamic action = actions.Item(1);
        return action.Type == ExecutableAction &&
            String.Equals((string)action.Path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            (string)action.Arguments == LaunchArgument;
    }

    internal static void MigratePreviousPath()
    {
        string executable = Application.ExecutablePath;
        if (!String.Equals(Path.GetFileName(executable), "FlickMeter.exe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            string sid;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
            dynamic scheduler = Connect();
            dynamic root = scheduler.GetFolder("\\");
            string name = TaskName(sid);
            dynamic task = FindTask(root, name);
            if (task == null || !task.Enabled) return;
            dynamic definition = task.Definition;
            if (definition.Actions.Count != 1 || definition.Principal.LogonType != InteractiveToken) return;
            dynamic action = definition.Actions.Item(1);
            string directory = Path.GetDirectoryName(executable);
            string previous = Path.Combine(directory, "GameOverlay-v1.0.exe");
            if (action.Type != ExecutableAction || (string)action.Arguments != LaunchArgument ||
                !String.Equals((string)action.Path, previous, StringComparison.OrdinalIgnoreCase)) return;
            action.Path = executable;
            action.WorkingDirectory = directory;
            definition.Principal.RunLevel = 0;
            definition.RegistrationInfo.Description = "登录 Windows 后启动 FlickMeter";
            root.RegisterTaskDefinition(name, definition, CreateOrUpdate, sid, null, InteractiveToken, null);
        }
        catch (Exception error) { Debug.WriteLine(error); }
    }

    internal static bool SetEnabled(bool enabled, bool elevated)
    {
        string sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User.Value;
        try
        {
            Apply(enabled, sid);
            return true;
        }
        catch (UnauthorizedAccessException) { if (elevated) throw; }
        catch (COMException ex) { if (elevated || ex.ErrorCode != AccessDenied) throw; }

        // 提权进程只接受发起操作的同一 Windows 用户。
        try
        {
            using (Process helper = Process.Start(new ProcessStartInfo(Application.ExecutablePath)
            {
                Arguments = (enabled ? EnableArgument : DisableArgument) + " " + sid,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                if (helper == null) return false;
                helper.WaitForExit();
                return helper.ExitCode == 0;
            }
        }
        catch (Win32Exception ex) { Debug.WriteLine(ex); return false; }
    }

    internal static int ApplyElevated(bool enabled, string sid)
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User.Value != sid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    return 1;
            }
            Apply(enabled, sid);
            return 0;
        }
        catch (Exception ex) { Debug.WriteLine(ex); return 1; }
    }

    private static object Connect()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
        scheduler.Connect();
        return scheduler;
    }

    private static string TaskName(string sid) { return "GameOverlay-Autostart-" + sid; }

    private static object FindTask(dynamic root, string name)
    {
        try { return root.GetTask(name); }
        catch (Exception ex) { if (IsMissingTask(ex)) return null; throw; }
    }

    private static bool IsMissingTask(Exception error)
    {
        return error.HResult == TaskMissing || error.HResult == TaskPathMissing;
    }

    private static void Apply(bool enabled, string sid)
    {
        dynamic scheduler = Connect();
        dynamic root = scheduler.GetFolder("\\");
        string name = TaskName(sid);
        if (!enabled)
        {
            try { root.DeleteTask(name, 0); }
            catch (Exception ex) { if (!IsMissingTask(ex)) throw; }
            return;
        }

        string executable = Application.ExecutablePath;
        dynamic definition = scheduler.NewTask(0);
        definition.RegistrationInfo.Description = "登录 Windows 后启动 FlickMeter";
        definition.Principal.UserId = sid;
        definition.Principal.LogonType = InteractiveToken;
        definition.Principal.RunLevel = 0;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = IgnoreNewInstance;
        dynamic trigger = definition.Triggers.Create(LogonTrigger);
        trigger.UserId = sid;
        dynamic action = definition.Actions.Create(ExecutableAction);
        action.Path = executable;
        action.Arguments = LaunchArgument;
        action.WorkingDirectory = Path.GetDirectoryName(executable);
        root.RegisterTaskDefinition(name, definition, CreateOrUpdate, sid, null, InteractiveToken, null);
    }
}
