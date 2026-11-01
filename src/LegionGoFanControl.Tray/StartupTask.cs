using System.Security.Principal;

namespace LegionGoFanControl.Tray;

// The task belongs to the signed-in user and launches the current executable at highest available privilege.
internal static class StartupTask
{
    private static string Name => "LegionGoFanControl-" + WindowsIdentity.GetCurrent().User!.Value;
    private static dynamic Folder()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(type)!; service.Connect(); return service.GetFolder("\\");
    }
    private static void ValidateOwned(dynamic registered)
    {
        dynamic definition = registered.Definition;
        string? marker = definition.RegistrationInfo.Description;
        string? principal = definition.Principal.UserId;
        int count = (int)definition.Actions.Count;
        string? actionPath = count == 1 ? (string?)definition.Actions.Item(1).Path : null;
        if (!StartupTaskOwnership.IsOwned(marker, principal, (int)definition.Principal.LogonType, count, actionPath, WindowsIdentity.GetCurrent().User!.Value, ResolveSid))
            throw new InvalidOperationException("A different scheduled task already uses the startup task name. It was not changed.");
    }
    private static string ResolveSid(string identity)
    {
        if (identity.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return new SecurityIdentifier(identity).Value;
        return ((SecurityIdentifier)new NTAccount(identity).Translate(typeof(SecurityIdentifier))).Value;
    }
    private static dynamic? Existing(dynamic folder)
    {
        object? registered = StartupTaskLookup.Find<object>(() => (object)folder.GetTask(Name));
        if (registered is not null) ValidateOwned((dynamic)registered);
        return registered;
    }
    public static bool IsEnabled() => Capture().Enabled;
    internal sealed record Snapshot(string? Xml, bool Enabled);
    public static Snapshot Capture()
    {
        dynamic folder = Folder(); dynamic? task = Existing(folder);
        return task is null ? new Snapshot(null, false) : new Snapshot((string)task.Xml, (bool)task.Enabled);
    }
    public static void Restore(Snapshot snapshot)
    {
        dynamic folder = Folder(); dynamic? existing = Existing(folder);
        if (snapshot.Xml is null) { if (existing is not null) folder.DeleteTask(Name, 0); return; }
        dynamic restored = folder.RegisterTask(Name, snapshot.Xml, 6, null, null, 3, null);
        restored.Enabled = snapshot.Enabled;
    }
    public static void SetEnabled(bool enabled)
    {
        dynamic folder = Folder();
        dynamic? existing = Existing(folder);
        if (!enabled)
        {
            if (existing is not null)
                StartupTaskLookup.Find<object>(() => { folder.DeleteTask(Name, 0); return new object(); });
            return;
        }
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");
        if (!Path.GetFileName(executable).Equals("LegionGoFanControl.Tray.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Startup requires the published LegionGoFanControl.Tray.exe.");
        Type type = Type.GetTypeFromProgID("Schedule.Service")!;
        dynamic service = Activator.CreateInstance(type)!; service.Connect();
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = StartupTaskOwnership.Marker;
        task.Principal.UserId = WindowsIdentity.GetCurrent().User!.Value;
        task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        task.Principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
        task.Settings.StartWhenAvailable = true;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        dynamic trigger = task.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = WindowsIdentity.GetCurrent().User!.Value;
        dynamic action = task.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = executable; action.WorkingDirectory = Path.GetDirectoryName(executable)!;
        folder.RegisterTaskDefinition(Name, task, 6, null, null, 3, null); // create or update; interactive token
    }
}
