using UnityEditor;

namespace VrcToVrm
{
    public enum UiLanguage { English, Korean, Japanese, ChineseSimplified }

    /// <summary>
    /// UI / log string lookup. Texts live in LocalizationTable (one row per key: en, ko, ja, zh-Hans).
    /// Missing translations fall back to English, then to the key itself.
    /// </summary>
    public static class L
    {
        const string PrefKey = "VrcToVrm.Language";

        /// <summary>Prefix of log lines that need the user's attention (detected by the result view).</summary>
        public const string WarnMark = "⚠ ";

        public static readonly string[] LanguageNames = { "English", "한국어", "日本語", "简体中文" };

        static UiLanguage? _language;

        public static UiLanguage Language
        {
            get => _language ?? (UiLanguage)(_language = (UiLanguage)EditorPrefs.GetInt(PrefKey, (int)UiLanguage.English));
            set
            {
                _language = value;
                EditorPrefs.SetInt(PrefKey, (int)value);
            }
        }

        public static string Tr(string key)
        {
            if (!LocalizationTable.Strings.TryGetValue(key, out var row)) return key;
            var i = (int)Language;
            if (i < row.Length && !string.IsNullOrEmpty(row[i])) return row[i];
            return string.IsNullOrEmpty(row[0]) ? key : row[0];
        }

        public static string Tr(string key, params object[] args)
        {
            var format = Tr(key);
            try { return string.Format(format, args); }
            catch (System.FormatException) { return format; } // a broken translation must never break the tool
        }

        /// <summary>A log line the result view shows with a warning icon.</summary>
        public static string Warn(string key, params object[] args) => WarnMark + Tr(key, args);
    }
}
