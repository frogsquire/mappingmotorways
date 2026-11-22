using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Mono.Cecil;
using UnityEngine;

namespace HotReload
{
    /// <summary>
    /// Loads plugin DLLs from BepInEx\scripts and reloads them in the running game, on a hotkey or whenever a DLL there
    /// changes. Like BepInEx.Debug's ScriptEngine, but the host object is hidden the way this game needs
    /// (HideManagerGameObject): otherwise the game destroys it and reloaded plugins never get Update().
    ///
    /// Each load renames the assembly in memory, so Mono treats it as new; old copies stay loaded (Mono can't unload),
    /// which is fine for development. Plugins are destroyed before reloading, so they should clean up in OnDestroy.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class HotReloadPlugin : BaseUnityPlugin
    {
        public const string Guid = "motorways.hotreload";
        public const string Name = "HotReload";
        public const string Version = "1.0.0";

        private const float PollSeconds = 0.5f;
        private const float SettleSeconds = 0.75f;
        private const int HideAndDontSave = 61; // what BepInEx uses for its own hidden manager object

        private ConfigEntry<string> _reloadKeyName;
        private ConfigEntry<bool> _autoReload;
        private KeyCode _reloadKey;

        private string _scriptsDir;
        private GameObject _host;
        private readonly List<string> _loadedGuids = new List<string>();
        private string _loadedSignature;
        private string _pendingSignature;
        private float _pendingSince;
        private float _nextPoll;
        private int _generation;

        private void Awake()
        {
            _reloadKeyName = Config.Bind("General", "ReloadKey", "F6",
                "Key that reloads every plugin in BepInEx\\scripts (a UnityEngine.KeyCode name). None disables it.");
            _autoReload = Config.Bind("General", "AutoReload", true,
                "Reload automatically when a DLL in BepInEx\\scripts is added, removed or rebuilt.");
            ParseKey();
            _reloadKeyName.SettingChanged += (_, __) => ParseKey();

            _scriptsDir = Path.Combine(Paths.BepInExRootPath, "scripts");
            Directory.CreateDirectory(_scriptsDir);
            Logger.LogInfo($"{Name} {Version} loaded; watching {_scriptsDir}.");
        }

        private void ParseKey()
        {
            try
            {
                _reloadKey = (KeyCode)Enum.Parse(typeof(KeyCode), _reloadKeyName.Value.Trim(), true);
            }
            catch (Exception)
            {
                Logger.LogWarning($"Unknown ReloadKey '{_reloadKeyName.Value}'; using F6.");
                _reloadKey = KeyCode.F6;
            }
        }

        // Start runs after the chainloader has finished, so normal plugins are already up.
        private void Start()
        {
            LoadAll();
        }

        private void Update()
        {
            if (_reloadKey != KeyCode.None && Input.GetKeyDown(_reloadKey))
            {
                Logger.LogInfo("Reload key pressed.");
                ReloadAll();
                return;
            }

            if (!_autoReload.Value || Time.unscaledTime < _nextPoll)
                return;
            _nextPoll = Time.unscaledTime + PollSeconds;

            string signature = ScriptsSignature();
            if (signature == _loadedSignature)
            {
                _pendingSignature = null;
                return;
            }

            // Wait until the files stop changing, so a build that's still copying isn't loaded half-written.
            if (signature != _pendingSignature)
            {
                _pendingSignature = signature;
                _pendingSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _pendingSince >= SettleSeconds)
            {
                Logger.LogInfo("Scripts changed on disk.");
                ReloadAll();
            }
        }

        private void OnDestroy()
        {
            UnloadAll();
        }

        private string[] ScriptFiles()
        {
            return Directory.Exists(_scriptsDir)
                ? Directory.GetFiles(_scriptsDir, "*.dll", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : new string[0];
        }

        private string ScriptsSignature()
        {
            var sb = new StringBuilder();
            foreach (string file in ScriptFiles())
            {
                var info = new FileInfo(file);
                sb.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            return sb.ToString();
        }

        private void ReloadAll()
        {
            UnloadAll();
            LoadAll();
        }

        private void UnloadAll()
        {
            if (_host != null)
            {
                // Immediate, so old plugins' OnDestroy cleans up before the new copies' Awake runs.
                DestroyImmediate(_host);
                _host = null;
            }
            foreach (string guid in _loadedGuids)
                Chainloader.PluginInfos.Remove(guid);
            _loadedGuids.Clear();
        }

        private void LoadAll()
        {
            _loadedSignature = ScriptsSignature();
            _pendingSignature = null;
            string[] files = ScriptFiles();
            if (files.Length == 0)
                return;

            _generation++;
            _host = new GameObject($"HotReload_Scripts_{_generation}");
            DontDestroyOnLoad(_host);
            _host.hideFlags = (HideFlags)HideAndDontSave;

            int count = 0;
            foreach (string file in files)
            {
                try
                {
                    count += LoadFile(file);
                }
                catch (Exception e)
                {
                    Logger.LogError($"Failed to load {Path.GetFileName(file)}: {e}");
                }
            }
            Logger.LogInfo($"Load #{_generation}: {count} plugin(s) from {files.Length} DLL(s).");
        }

        private int LoadFile(string file)
        {
            Assembly assembly = LoadRenamed(file);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                foreach (Exception le in e.LoaderExceptions.Where(x => x != null).Take(5))
                    Logger.LogWarning($"{Path.GetFileName(file)}: {le.Message}");
                types = e.Types.Where(t => t != null).ToArray();
            }

            int count = 0;
            foreach (Type type in types)
            {
                if (type.IsAbstract || !typeof(BaseUnityPlugin).IsAssignableFrom(type))
                    continue;
                var meta = (BepInPlugin)type.GetCustomAttributes(typeof(BepInPlugin), false).FirstOrDefault();
                if (meta == null)
                    continue;

                if (Chainloader.PluginInfos.ContainsKey(meta.GUID) && !_loadedGuids.Contains(meta.GUID))
                {
                    Logger.LogWarning($"{meta.Name} is already loaded from BepInEx\\plugins; remove it from there to hot-reload it.");
                    continue;
                }

                try
                {
                    var plugin = (BaseUnityPlugin)_host.AddComponent(type);
                    Chainloader.PluginInfos[meta.GUID] = plugin.Info;
                    _loadedGuids.Add(meta.GUID);
                    count++;
                    Logger.LogInfo($"Loaded {meta.Name} {meta.Version} from scripts.");
                }
                catch (Exception e)
                {
                    Logger.LogError($"Failed to start {meta.Name}: {e}");
                }
            }
            return count;
        }

        /// <summary>
        /// Loads a copy of the DLL under a unique assembly name (Mono would otherwise hand back the copy it already has).
        /// Reads the bytes up front so the file isn't locked and the next build can overwrite it. No debug symbols:
        /// this game's Mono can't load them from memory, so hot-loaded plugins' stack traces have no line numbers.
        /// </summary>
        private Assembly LoadRenamed(string file)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Paths.ManagedPath);
                resolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);
                resolver.AddSearchDirectory(Paths.PluginPath);
                resolver.AddSearchDirectory(_scriptsDir);

                var input = new MemoryStream(File.ReadAllBytes(file));
                using (AssemblyDefinition def = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    def.Name.Name = $"{def.Name.Name}-hot{_generation}-{DateTime.Now.Ticks}";
                    var output = new MemoryStream();
                    def.Write(output);
                    return Assembly.Load(output.ToArray());
                }
            }
        }
    }
}
