using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NetworkRescue
{
    internal static class LocalizationTests
    {
        private static int assertions;

        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }

        private static int RunIsolated()
        {
            string directory = Path.Combine(Path.GetTempPath(), "NetworkRescueLocalizationTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string executable = Path.Combine(directory, "LocalizationTests.exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, executable);
                using (Process child = new Process())
                {
                    child.StartInfo = new ProcessStartInfo(executable, "--isolated") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    child.Start();
                    Task<string> output = child.StandardOutput.ReadToEndAsync();
                    Task<string> errors = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(20000)) { child.Kill(); throw new TimeoutException("Localization test child timed out."); }
                    Console.Write(output.GetAwaiter().GetResult());
                    Console.Error.Write(errors.GetAwaiter().GetResult());
                    return child.ExitCode;
                }
            }
            finally
            {
                string full = Path.GetFullPath(directory);
                string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (String.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("NetworkRescueLocalizationTests-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 1 || args[0] != "--isolated") return RunIsolated();
                Assert(I18n.Language == "zh-CN", "default language remains Simplified Chinese");
                Assert(I18n.T("中文文本") == "中文文本", "Chinese source is unchanged");
                Assert(I18n.T(null) == null && I18n.T("") == "", "null and empty source are preserved");
                string resourceRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i18n");
                Directory.CreateDirectory(resourceRoot);
                string source = "状态：{0} / %s / ${HOST}\r\n下一行";
                foreach (string code in new[] { "zh-TW", "en", "ja", "ko", "de", "fr", "es", "pt", "ru" })
                {
                    Dictionary<string, string> messages = new Dictionary<string, string>
                    {
                        { source, code + " status: {0} / %s / ${HOST}\r\nnext line" },
                        { "用户网卡：{0}", code + " adapter: {0}" },
                        { "中文的目标", code + " translated target" },
                        { "Read", code + " uppercase" },
                        { "空翻译", "" }
                    };
                    File.WriteAllText(Path.Combine(resourceRoot, "messages." + code + ".json"), new JavaScriptSerializer().Serialize(messages), new UTF8Encoding(false));
                    I18n.SetLanguage(code);
                    Assert(I18n.Language == code && I18n.T(source) == messages[source], "supported language loads exact UTF-8 dictionary: " + code);
                    Assert(I18n.T("read") == "read", "lookup is ordinal and case sensitive");
                    Assert(I18n.T("未知键") == "未知键" && I18n.T("空翻译") == "空翻译", "missing and empty translation fall back to source");
                    string dynamic = @"办公室网卡 中文的目标 C:\Users\中文的目标";
                    Assert(I18n.T(dynamic) == dynamic, "whole user data is never changed by substring replacement");
                    Assert(String.Format(I18n.T("用户网卡：{0}"), dynamic) == code + " adapter: " + dynamic, "formatting keeps dynamic adapter and path data intact");
                    Assert(I18n.T(source).Contains("{0} / %s / ${HOST}\r\n"), "composite placeholder, printf marker, macro and CRLF survive translation unchanged");
                }
                I18n.SetLanguage("zh-CN");
                Assert(I18n.T(source) == source, "returning to Chinese bypasses dictionary translation");
                foreach (string invalid in new[] { "", "EN", "zh", "it", "../en", "en/../../file", null })
                {
                    bool rejected = false;
                    try { I18n.SetLanguage(invalid); }
                    catch (ArgumentException) { rejected = true; }
                    Assert(rejected && I18n.Language == "zh-CN", "invalid language rejected without changing state");
                }
                File.Delete(Path.Combine(resourceRoot, "messages.ko.json"));
                I18n.SetLanguage("ko");
                Assert(I18n.Language == "ko" && I18n.T(source) == source, "missing resource safely falls back");
                File.WriteAllText(Path.Combine(resourceRoot, "messages.ja.json"), "{not valid JSON", new UTF8Encoding(false));
                I18n.SetLanguage("ja");
                Assert(I18n.T(source) == source, "malformed resource safely falls back");
                File.WriteAllText(Path.Combine(resourceRoot, "messages.zh-TW.json"), "{\"中文的目标\":17,\"合法键\":\"valid value\"}", new UTF8Encoding(false));
                I18n.SetLanguage("zh-TW");
                Assert(I18n.T("中文的目标") == "中文的目标" && I18n.T("合法键") == "valid value", "non-string resource values are ignored");
                File.WriteAllText(Path.Combine(resourceRoot, "messages.ko.json"), new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { source, "ko atomic" } }), new UTF8Encoding(false));
                int corrupt = 0;
                Task writer = Task.Run(delegate { for (int i = 0; i < 100; i++) I18n.SetLanguage(i % 2 == 0 ? "en" : "ko"); });
                Task[] readers = Enumerable.Range(0, 4).Select(delegate(int unused)
                {
                    return Task.Run(delegate
                    {
                        for (int i = 0; i < 4000; i++)
                        {
                            string text = I18n.T(source);
                            if (text != source && text != "ko atomic" && text != "en status: {0} / %s / ${HOST}\r\nnext line") Interlocked.Increment(ref corrupt);
                        }
                    });
                }).ToArray();
                Task.WaitAll(readers.Concat(new[] { writer }).ToArray());
                Assert(corrupt == 0, "concurrent readers only observe complete immutable dictionaries");
                I18n.SetLanguage("zh-CN");
                Console.WriteLine("PASS: " + assertions + " localization assertions. Isolated resources only; no system settings changed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
    }
}
