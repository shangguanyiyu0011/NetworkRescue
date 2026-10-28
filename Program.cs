using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Principal;

[assembly: AssemblyTitle("断网急救箱")]
[assembly: AssemblyDescription("先诊断，再修复的 Windows 网络工具")]
[assembly: AssemblyProduct("断网急救箱")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace NetworkRescue
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        internal static void ConfigureNetworkPolicy()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            // Probes inspect error headers only; buffering a stalled CONNECT error body hides its status.
            HttpWebRequest.DefaultMaximumErrorResponseLength = 0;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // The inbox Framework compiler's HTTP stack rejects SystemDefault on supported Windows hosts.
            ConfigureNetworkPolicy();
            bool scan = false;
            string output = null, owner = null, language = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--scan" && !scan) scan = true;
                else if (args[i] == "--diagnose" && output == null && i + 1 < args.Length) output = args[++i];
                else if (args[i] == "--owner-sid" && owner == null && i + 1 < args.Length) owner = args[++i];
                else if (args[i] == "--language" && language == null && i + 1 < args.Length) language = args[++i];
                else return 2;
            }
            if ((scan && output != null) || (owner != null && (!scan || owner.Length > 128))) return 2;
            try { if (language != null) I18n.SetLanguage(language); } catch (ArgumentException) { return 2; }
            if (owner != null && !String.Equals(owner, WindowsIdentity.GetCurrent().User.Value, StringComparison.Ordinal))
            {
                SoftDialog.Show(null, NativeUiText.LocalText("native.crossAccount"), NativeUiText.LocalText("native.permissionTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
            // This entry point only diagnoses; repairs are available only through the reviewed UI flow.
            if (output != null)
            {
                try
                {
                    var report = DiagnosticService.DiagnoseAsync(new RescueEngine(), null, CancellationToken.None).GetAwaiter().GetResult();
                    File.WriteAllText(Path.GetFullPath(output), ReportRedactor.Redact(report.ToPlainText()), new UTF8Encoding(true));
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(Path.GetFullPath(output) + ".error.txt", ex.Message, new UTF8Encoding(true)); } catch { }
                    return 1;
                }
            }
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                SoftDialog.Show(null, String.Format(NativeUiText.LocalText("native.failure"), e.Exception.Message), NativeUiText.LocalText("native.errorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            // Keep the utility visible on graphics drivers that fail WPF's hardware composition path.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var application = new System.Windows.Application();
            var window = new NativeWindow();
            var controller = new NativeController(window, scan, language);
            application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                SoftDialog.Show(null, String.Format(NativeUiText.LocalText("native.failure"), e.Exception.Message), NativeUiText.LocalText("native.errorTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            application.Run(window);
            return 0;
        }
    }
}
