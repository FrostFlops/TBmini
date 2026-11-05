using System;
using System.Globalization;
using Microsoft.Win32;

namespace TBMini
{
    public enum LangPref { Auto = 0, Zh = 1, En = 2 }

    /// UI language: Chinese or English, chosen manually or following the Windows display language.
    /// Strings are written inline as T("中文", "English").
    public static class L10n
    {
        const string Key = @"Software\TBMini", Value = "Language";

        /// Windows display language at startup (changing it needs a sign-out, which restarts us anyway).
        public static readonly bool SystemZh =
            CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        public static LangPref Pref { get; private set; } = LoadPref();
        public static bool Zh { get; private set; } = Resolve(Pref);

        public static string T(string zh, string en) => Zh ? zh : en;

#if TBMINI_DEV
        public static void ForceForDev(bool zh) => Zh = zh;   // screenshot hook only (not saved)
#endif

        static bool Resolve(LangPref p) => p == LangPref.Zh || (p == LangPref.Auto && SystemZh);

        static LangPref LoadPref()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                return (k?.GetValue(Value) as string)?.ToLowerInvariant() switch
                {
                    "zh" => LangPref.Zh,
                    "en" => LangPref.En,
                    _ => LangPref.Auto,
                };
            }
            catch { return LangPref.Auto; }
        }

        /// Saves the preference. Returns true if the effective language changed.
        public static bool Set(LangPref p)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(Value, p == LangPref.Zh ? "zh" : p == LangPref.En ? "en" : "auto");
            bool was = Zh;
            Pref = p;
            Zh = Resolve(p);
            return was != Zh;
        }
    }
}
