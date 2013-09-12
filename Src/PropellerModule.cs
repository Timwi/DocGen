using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.PropellerApi;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;
using RT.Util.Serialization;

namespace RT.DocGen
{
    public class DocGenPropellerModule : MarshalByRefObject, IPropellerModule
    {
        string IPropellerModule.Name
        {
            get
            {
                return "Documentation Generator";
            }
        }

        class Settings
        {
            [ClassifyNotNull]
            public string[] Paths = new string[] { };
            public bool RequireAuthentication = true;
            public string DllTempPath = null;
            public string UsernamePasswordFile = null;
        }

        private Settings _settings;
        private LoggerBase _log;
        private ISettingsSaver _saver;
        private DocumentationGenerator _docGen;

        void IPropellerModule.Init(LoggerBase log, JsonValue settings, ISettingsSaver saver)
        {
            _log = log;
            _saver = saver;
            _settings = ClassifyJson.Deserialize<Settings>(settings) ?? new Settings();

            var validPaths = new List<string>();

            foreach (var path in _settings.Paths)
                if (!Directory.Exists(path))
                    _log.Warn(@"DocGen: Warning: The folder ""{0}"" specified in the settings does not exist. Ignoring path.".Fmt(path));
                else
                    validPaths.Add(path);
            _settings.Paths = validPaths.ToArray();

            saver.SaveSettings(ClassifyJson.Serialize(_settings));

            // Try to clean up old folders we've created before
            var tempPath = _settings.DllTempPath ?? Path.GetTempPath();
            Directory.CreateDirectory(tempPath);
            foreach (var path in Directory.GetDirectories(tempPath, "docgen-tmp-*"))
            {
                try { Directory.Delete(path, true); }
                catch { }
            }

            // Find a new folder to put the DLL files into
            int j = 1;
            var copyToPath = Path.Combine(tempPath, "docgen-tmp-" + j);
            while (Directory.Exists(copyToPath))
            {
                j++;
                copyToPath = Path.Combine(tempPath, "docgen-tmp-" + j);
            }
            Directory.CreateDirectory(copyToPath);

            _docGen = new DocumentationGenerator(_settings.Paths, _settings.RequireAuthentication ? _settings.UsernamePasswordFile ?? "" : null, copyToPath);
            lock (_log)
            {
                _log.Info("DocGen initialised with {0} assemblies: {1}".Fmt(_docGen.AssembliesLoaded.Count, _docGen.AssembliesLoaded.JoinString(", ")));
                if (_docGen.AssemblyLoadErrors.Count > 0)
                {
                    _log.Warn("{0} assembly load errors:".Fmt(_docGen.AssemblyLoadErrors.Count));
                    foreach (var tuple in _docGen.AssemblyLoadErrors)
                        _log.Warn("{0} error: {1}".Fmt(tuple.Item1, tuple.Item2));
                }
            }
        }

        string[] IPropellerModule.FileFiltersToBeMonitoredForChanges
        {
            get
            {
                return
                    _settings.Paths.Select(p => Path.Combine(p, "*.dll")).Concat(
                    _settings.Paths.Select(p => Path.Combine(p, "*.xml"))).ToArray();
            }
        }

        HttpResponse IPropellerModule.Handle(HttpRequest req) { return _docGen.Handle(req); }
        bool IPropellerModule.MustReinitialize { get { return false; } }
        void IPropellerModule.Shutdown() { }
    }
}
