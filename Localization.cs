using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using System.Web.Script.Serialization;

namespace NetworkRescue
{
    public static class I18n
    {
        private sealed class TranslationState
        {
            public readonly string Language;
            public readonly Dictionary<string, string> Messages;

            public TranslationState(string language, Dictionary<string, string> messages)
            {
                Language = language;
                Messages = messages;
            }
        }

        private static volatile TranslationState current = new TranslationState("zh-CN", new Dictionary<string, string>(StringComparer.Ordinal));

        public static string Language { get { return current.Language; } }

        public static void SetLanguage(string language)
        {
            if (language != "zh-CN" && language != "zh-TW" && language != "en" && language != "ja" && language != "ko"
                && language != "de" && language != "fr" && language != "es" && language != "pt" && language != "ru")
                throw new ArgumentException("Unsupported language code.", "language");
            Dictionary<string, string> messages = new Dictionary<string, string>(StringComparer.Ordinal);
            if (language != "zh-CN") messages = ReadMessages(language);
            current = new TranslationState(language, messages);
        }

        public static string T(string source)
        {
            if (source == null) return null;
            TranslationState state = current;
            string translated;
            return state.Language != "zh-CN" && state.Messages.TryGetValue(source, out translated) && !String.IsNullOrEmpty(translated) ? translated : source;
        }

        private static Dictionary<string, string> ReadMessages(string language)
        {
            Dictionary<string, string> messages = new Dictionary<string, string>(StringComparer.Ordinal);
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "i18n", "messages." + language + ".json");
            try
            {
                FileInfo file = new FileInfo(path);
                if (!file.Exists || file.Length > 4 * 1024 * 1024) return messages;
                string json = File.ReadAllText(path, new UTF8Encoding(false, true));
                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 };
                Dictionary<string, object> parsed = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (parsed != null)
                    foreach (KeyValuePair<string, object> entry in parsed)
                        if (entry.Value is string && !String.IsNullOrEmpty((string)entry.Value)) messages[entry.Key] = (string)entry.Value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return messages;
        }
    }
}
