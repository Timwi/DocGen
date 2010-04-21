using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RT.PropellerApi;
using RT.Servers;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Xml;

namespace RT.DocGen
{
    public class DocGenPropellerModule : MarshalByRefObject, IPropellerModule
    {
        class Settings
        {
            public string Url = "/doc";
            public string[] Paths = new string[] { };
            public bool RequireAuthentication = true;
            [XmlIgnoreIfDefault]
            public string UsernamePasswordFile = null;
        }

        private Settings _settings;
        private DocumentationGenerator _docGen;
        private string _configFilePath;
        private LoggerBase _log;

        public string GetName() { return "DocGen"; }

        public PropellerModuleInitResult Init(string origDllPath, string tempDllPath, LoggerBase log)
        {
            _log = log;
            _configFilePath = Path.Combine(Path.GetDirectoryName(origDllPath), Path.GetFileNameWithoutExtension(origDllPath) + ".config.xml");
            loadSettings();

            foreach (var invalid in _settings.Paths.Where(d => !Directory.Exists(d)))
                lock (_log)
                    _log.Warn(@"DocGen: Warning: The folder ""{0}"" specified in the configuration file ""{1}"" does not exist. Ignoring path.".Fmt(invalid, _configFilePath));

            // Try to clean up old folders we've created before
            var tempPath = Path.GetTempPath();
            foreach (var pth in Directory.GetDirectories(tempPath, "docgen-tmp-*"))
            {
                foreach (var file in Directory.GetFiles(pth))
                    try { File.Delete(file); }
                    catch { }
                try { Directory.Delete(pth); }
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

            var paths = _settings.Paths.Where(d => Directory.Exists(d)).ToArray();
            _docGen = new DocumentationGenerator(paths, _settings.RequireAuthentication ? _settings.UsernamePasswordFile ?? "" : null, copyToPath);
            lock (_log)
            {
                _log.Info("DocGen initialised.");
                _log.Info("{0} assemblies loaded: {1}".Fmt(_docGen.AssembliesLoaded.Count, _docGen.AssembliesLoaded.JoinString(", ")));
                if (_docGen.AssemblyLoadErrors.Count > 0)
                {
                    _log.Warn("{0} assembly load errors:".Fmt(_docGen.AssemblyLoadErrors.Count));
                    foreach (var tuple in _docGen.AssemblyLoadErrors)
                        _log.Warn("{0} error: {1}".Fmt(tuple.E1, tuple.E2));
                }
            }
            var hooks = new List<HttpRequestHandlerHook>();

            if (string.IsNullOrEmpty(_settings.Url))
                hooks.Add(new HttpRequestHandlerHook(_docGen.Handler));
            else
                hooks.Add(new HttpRequestHandlerHook(_docGen.Handler, path: _settings.Url));

            return new PropellerModuleInitResult
            {
                HandlerHooks = hooks,
                FileFiltersToBeMonitoredForChanges = paths.Select(p => Path.Combine(p, "*.dll")).Concat(paths.Select(p => Path.Combine(p, "*.xml"))).Concat(_configFilePath)
            };
        }

        private void loadSettings()
        {
            try
            {
                _settings = XmlClassify.LoadObjectFromXmlFile<Settings>(_configFilePath);
            }
            catch (Exception e)
            {
                if (File.Exists(_configFilePath))
                {
                    lock (_log)
                    {
                        _log.Warn("DocGen: Error reading configuration file: {0}".Fmt(_configFilePath));
                        _log.Warn(e.Message);
                    }

                    string renameTo = _configFilePath;
                    int i = 1;
                    while (File.Exists(renameTo))
                    {
                        i++;
                        renameTo = Path.Combine(Path.GetDirectoryName(_configFilePath), Path.GetFileNameWithoutExtension(_configFilePath) + " (" + i + ")" + Path.GetExtension(_configFilePath));
                    }
                    try
                    {
                        File.Move(_configFilePath, renameTo);
                    }
                    catch (Exception e2)
                    {
                        lock (_log)
                        {
                            _log.Warn("DocGen: Error renaming configuration file to: {0}".Fmt(renameTo));
                            _log.Warn(e2.Message);
                        }
                        return;
                    }
                    lock (_log)
                        _log.Warn(@"DocGen: Configuration file renamed to ""{0}"".".Fmt(renameTo));
                }
                lock (_log)
                    _log.Info("DocGen: Creating new configuration file with default values...");

                var newPath = Path.Combine(Path.GetDirectoryName(_configFilePath), "DocGen");
                try
                {
                    Directory.CreateDirectory(newPath);
                }
                catch (Exception e3)
                {
                    lock (_log)
                    {
                        _log.Error("DocGen: Error creating directory: {0}".Fmt(newPath));
                        _log.Error(e3.Message);
                    }
                }
                _settings = new Settings { Paths = new string[] { newPath } };
                try
                {
                    XmlClassify.SaveObjectToXmlFile(_settings, _configFilePath);
                }
                catch (Exception e3)
                {
                    lock (_log)
                    {
                        _log.Error("DocGen: Error saving configuration file: {0}".Fmt(_configFilePath));
                        _log.Error(e3.Message);
                    }
                }
            }
        }

        public bool MustReinitServer()
        {
            return false;
        }

        public void Shutdown()
        {
        }
    }
}
