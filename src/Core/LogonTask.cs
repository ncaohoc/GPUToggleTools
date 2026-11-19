// Starts GPU Toggle when the user signs in, through a Task Scheduler task that runs with highest privileges.
// Windows asks for admin rights once, when the task is created; after that the app starts at every sign-in
// without a UAC prompt. It starts after sign-in (after the PIN), hidden in the notification area.

using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace GpuToggle
{
    static class LogonTask
    {
        public const string Name = "GPUToggle";
        public const string StartupArg = "--startup";

        static bool? cached;

        // True when the task exists (cached; Create / Delete update it).
        public static bool Enabled
        {
            get
            {
                if (!cached.HasValue)
                {
                    string xml;
                    cached = Query(out xml);
                }
                return cached.Value;
            }
        }

        static bool Query(out string xml)
        {
            return Shell.Run("schtasks.exe", "/Query /TN " + Name + " /XML", 15000, out xml) == 0;
        }

        static string Exe { get { return Application.ExecutablePath; } }

        // The exe was moved or renamed since the task was made: point the task at this copy.
        public static void FixPath()
        {
            string xml;
            if (!Query(out xml)) { cached = false; return; }
            cached = true;
            if (xml.IndexOf(SecurityElement.Escape(Exe), StringComparison.OrdinalIgnoreCase) < 0)
            {
                string err;
                Create(out err);
            }
        }

        public static bool Create(out string error)
        {
            error = null;
            string user = WindowsIdentity.GetCurrent().Name;
            string q = "'";
            string xml =
                "<?xml version=" + q + "1.0" + q + " encoding=" + q + "UTF-16" + q + "?>" + Txt.NL +
                "<Task version=" + q + "1.2" + q + " xmlns=" + q + "http://schemas.microsoft.com/windows/2004/02/mit/task" + q + ">" + Txt.NL +
                "  <RegistrationInfo><Description>GPU Toggle: enables the eGPU after sign-in.</Description></RegistrationInfo>" + Txt.NL +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" + Txt.NL +
                "  <Principals><Principal id=" + q + "Author" + q + "><UserId>" + SecurityElement.Escape(user) + "</UserId>" +
                "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" + Txt.NL +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>" + Txt.NL +
                "  <Actions Context=" + q + "Author" + q + "><Exec><Command>" + SecurityElement.Escape(Exe) + "</Command>" +
                "<Arguments>" + StartupArg + "</Arguments></Exec></Actions>" + Txt.NL +
                "</Task>" + Txt.NL;

            string file = Path.Combine(Path.GetTempPath(), "GPUToggle-task.xml");
            try
            {
                File.WriteAllText(file, xml, Encoding.Unicode);
                string output;
                int code = Shell.Run("schtasks.exe", "/Create /TN " + Name + " /XML " + Txt.Q + file + Txt.Q + " /F", 15000, out output);
                if (code != 0) { error = Shell.LastLine(output); return false; }
                cached = true;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            finally { try { File.Delete(file); } catch { } }
        }

        public static bool Delete(out string error)
        {
            error = null;
            string output;
            int code = Shell.Run("schtasks.exe", "/Delete /TN " + Name + " /F", 15000, out output);
            string ignored;
            if (code != 0 && Query(out ignored)) { error = Shell.LastLine(output); return false; }
            cached = false;
            return true;
        }
    }
}
