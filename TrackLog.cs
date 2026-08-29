using System;
using System.IO;

namespace PoPTracker
{
    // Writes straight to its own file instead of going through BepInEx's shared
    // LogOutput.log, so discovery-session output stays separate and easy to grep
    // through without wading past chainloader/startup noise from every plugin.
    public static class TrackLog
    {
        private static readonly object _lock = new object();
        private static string _path;

        public static void Init(string pluginDirectory)
        {
            _path = Path.Combine(pluginDirectory, "PoPTracker.log");
            lock (_lock)
            {
                File.WriteAllText(_path, $"=== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
            }
        }

        public static void Log(string message)
        {
            if (_path == null) return; // Init() wasn't called — fail quietly rather than crash a hook
            lock (_lock)
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
        }
    }
}