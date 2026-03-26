using RT.Json;
using RT.PropellerApi;
using RT.Serialization;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace RT.DocGen
{
    public class DocGenPropellerModule : IPropellerModule
    {
        string IPropellerModule.Name => "Documentation Generator";

        private class Settings
        {
            [ClassifyNotNull]
            public string[] Paths = [];
            public bool RequireAuthentication = true;
            public string DllTempPath = null;
            public string UsernamePasswordFile = null;
        }

        private Settings _settings;
        private LoggerBase _log;
        private DocumentationGenerator _docGen;

        void IPropellerModule.Init(LoggerBase log, JsonValue settings, ISettingsSaver saver)
        {
            _log = log;
            _settings = ClassifyJson.Deserialize<Settings>(settings) ?? new Settings();
            saver.SaveSettings(ClassifyJson.Serialize(_settings));

            var validPaths = new List<string>();

            foreach (var path in _settings.Paths)
                if (!Directory.Exists(path))
                    _log.Warn($@"DocGen: Warning: The folder ""{path}"" specified in the settings does not exist. Ignoring path.");
                else
                    validPaths.Add(path);
            _settings.Paths = validPaths.ToArray();

            saver.SaveSettings(ClassifyJson.Serialize(_settings));

            string copyToPath = null;
            if (_settings.DllTempPath != null)
            {
                // Try to clean up old folders we've created before
                var tempPath = _settings.DllTempPath;
                Directory.CreateDirectory(tempPath);
                foreach (var path in Directory.GetDirectories(tempPath, "docgen-tmp-*"))
                {
                    try { Directory.Delete(path, true); }
                    catch { }
                }

                // Find a new folder to put the DLL files into
                var j = 1;
                copyToPath = Path.Combine(tempPath, "docgen-tmp-" + j);
                while (Directory.Exists(copyToPath))
                {
                    j++;
                    copyToPath = Path.Combine(tempPath, "docgen-tmp-" + j);
                }
                Directory.CreateDirectory(copyToPath);
            }

            _docGen = new DocumentationGenerator(_settings.Paths, _settings.RequireAuthentication ? _settings.UsernamePasswordFile ?? "" : null, copyToPath);
            lock (_log)
            {
                _log.Info($"DocGen initialized with {_docGen.AssembliesLoaded.Count} assemblies.");
                if (_docGen.AssemblyLoadErrors.Count > 0)
                {
                    _log.Warn($"{_docGen.AssemblyLoadErrors.Count} assembly load errors:");
                    foreach (var tuple in _docGen.AssemblyLoadErrors)
                        _log.Warn($"{tuple.Item1} error: {tuple.Item2}");
                }
            }
        }

        string[] IPropellerModule.FileFiltersToBeMonitoredForChanges => [
            .._settings.Paths.Select(p => Path.Combine(p, "*.dll")),
            .._settings.Paths.Select(p => Path.Combine(p, "*.xml"))];

        HttpResponse IPropellerModule.Handle(HttpRequest req) => _docGen.Handle(req);
        bool IPropellerModule.MustReinitialize => false;
        void IPropellerModule.Shutdown() { }
    }
}
