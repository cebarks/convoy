// ponytail: minimal BepInEx type stubs for CI compilation
using System;
using System.Collections.Generic;
using System.IO;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version) { }
    }

    public abstract class BaseUnityPlugin : UnityEngine.MonoBehaviour
    {
        public BepInEx.Logging.ManualLogSource Logger { get; } = new BepInEx.Logging.ManualLogSource("");
        public Configuration.ConfigFile Config { get; } = new Configuration.ConfigFile("", false);
    }

    public static class Paths
    {
        public static string GameRootPath => "";
        public static string ConfigPath => "";
    }
}

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        public ManualLogSource(string name) { }
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
        public void LogDebug(object data) { }
    }
}

namespace BepInEx.Configuration
{
    public class ConfigFile
    {
        public ConfigFile(string path, bool save) { }
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description = "") => new ConfigEntry<T>();
    }

    public class ConfigEntry<T>
    {
        public T Value { get; set; } = default!;
    }
}
