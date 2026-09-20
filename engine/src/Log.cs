using System;
using BepInEx.Logging;

namespace PluginPassEngine
{
    internal static class Log
    {
        internal static ManualLogSource Source;
        internal static bool Verbose;

        internal static void Init(ManualLogSource src) { Source = src; }

        internal static void Info(string msg)
        {
            if (Source != null) Source.LogInfo(msg);
        }

        internal static void Warn(string msg)
        {
            if (Source != null) Source.LogWarning(msg);
        }

        internal static void Error(string msg)
        {
            if (Source != null) Source.LogError(msg);
        }

        internal static void Debug(string msg)
        {
            if (Verbose && Source != null) Source.LogInfo(msg);
        }

        /// <summary>Header used for the start-up and per-scene blocks.</summary>
        internal static void Rule(string title)
        {
            if (Source == null) return;
            string bar = new string('=', Math.Max(4, 74 - title.Length));
            Source.LogInfo("=== " + title + " " + bar);
        }
    }
}
