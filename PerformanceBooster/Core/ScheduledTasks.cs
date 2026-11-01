using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace PerformanceBooster.Core;

// Windows Task Scheduler through schtasks.exe. The app's tasks run in the user's session with the highest rights the user has:
// the RAM cleaner (started by the app, no trigger) and, when the app itself runs as administrator, the start at sign-in
// (Windows does not start a program that asks for administrator rights from the Run key).
internal static class ScheduledTasks
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Xml(string description, string exe, string arguments, string userSid, string? logonUser)
    {
        var trigger = logonUser is null ? "" : $"""

              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(logonUser)}</UserId>
                </LogonTrigger>
              </Triggers>
            """;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{SecurityElement.Escape(description)}</Description>
              </RegistrationInfo>{trigger}
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static string CurrentUserSid => WindowsIdentity.GetCurrent().User!.Value;

    public static string CurrentUserName => WindowsIdentity.GetCurrent().Name;

    // Creating needs administrator rights; schtasks reads the definition from a UTF-16 file.
    public static bool Create(string name, string xml)
    {
        var file = Path.Combine(Path.GetTempPath(), $"{AppPaths.Id}-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            return Run($"/Create /TN \"{name}\" /XML \"{file}\" /F", out _) == 0;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool Delete(string name) => Run($"/Delete /TN \"{name}\" /F", out _) == 0;

    // Starting works without administrator rights once the task exists.
    public static bool Start(string name) => Run($"/Run /TN \"{name}\"", out _) == 0;

    // The exe the task starts, or null if there is no such task.
    public static string? Command(string name)
    {
        if (Run($"/Query /TN \"{name}\" /XML", out var xml) != 0) return null;
        try
        {
            return XDocument.Parse(xml).Descendants(Ns + "Command").FirstOrDefault()?.Value.Trim('"');
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    static int Run(string args, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Shell.ConsoleEncoding, StandardErrorEncoding = Shell.ConsoleEncoding,
            });
            if (p is null) return -1;
            var error = p.StandardError.ReadToEndAsync();
            output = p.StandardOutput.ReadToEnd();
            _ = error.Result;
            return p.WaitForExit(15000) ? p.ExitCode : -1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
