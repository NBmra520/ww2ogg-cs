using System;
using System.Reflection;

namespace ww2ogg
{
    // Single source of truth for the tool version: taken from <Version> in the
    // project file so the banner and the Vorbis vendor string cannot drift apart.
    internal static class AppVersion
    {
        public static string Current { get; } = Read();

        private static string Read()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();

            string v = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrEmpty(v))
            {
                v = assembly.GetName().Version?.ToString();
            }

            if (string.IsNullOrEmpty(v)) return "unknown";

            // strip SourceLink build metadata (0.24+abcdef)
            int plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }
    }
}
