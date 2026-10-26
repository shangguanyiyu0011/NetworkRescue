using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace NetworkRescue
{
    internal static class NativeUiText
    {
        private static readonly object Sync = new object();
        private static string language;
        private static Dictionary<string, string> values = new Dictionary<string, string>();

        internal static string LocalText(string key)
        {
            lock (Sync)
            {
                if (language != I18n.Language)
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "locales", I18n.Language + ".json");
                    try
                    {
                        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
                        values = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.Deserialize<Dictionary<string, string>>(File.ReadAllText(path, new UTF8Encoding(false, true)));
                        language = I18n.Language;
                    }
                    catch { values = new Dictionary<string, string>(); language = null; }
                }
                string value;
                return values != null && values.TryGetValue(key, out value) ? value : key;
            }
        }
    }
}
