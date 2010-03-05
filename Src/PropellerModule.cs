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
        public class Settings
        {
            public string Url = "/doc";
            public string[] Paths = new string[] { };
        }

        private class documentable
        {
            public string DllPath;
            public string XmlPath;
            public DateTime DllLastChange;
            public DateTime XmlLastChange;
            public override bool Equals(object obj)
            {
                if (!(obj is documentable))
                    return false;
                var d = (documentable) obj;
                return DllPath == d.DllPath && XmlPath == d.XmlPath && DllLastChange == d.DllLastChange && XmlLastChange == d.XmlLastChange;
            }
            public override int GetHashCode()
            {
                return DllPath.GetHashCode() + XmlPath.GetHashCode() + DllLastChange.GetHashCode() + XmlLastChange.GetHashCode();
            }
        }

        private Settings _settings;
        private DocumentationGenerator _docGen;
        private List<documentable> _docs;
        private string[] _paths;
        private string _configFilePath;
        private LoggerBase _log;

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

            _paths = _settings.Paths.Where(d => Directory.Exists(d)).ToArray();
            _docs = getDocList();

            _docGen = new DocumentationGenerator(_paths, copyToPath);
            lock (_log)
                _log.Info("DocGen: Initialised successfully.");
            return new PropellerModuleInitResult
            {
                HandlerHooks = new HttpRequestHandlerHook[] { new HttpRequestHandlerHook(_settings.Url, _docGen.GetRequestHandler()) },
                FileFiltersToBeMonitoredForChanges = _paths.Select(p => Path.Combine(p, "*.dll")).Concat(_paths.Select(p => Path.Combine(p, "*.xml"))).Concat(_configFilePath)
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
                    _log.Warn("DocGen: Creating new configuration file with default values.");
                var newPath = Path.Combine(Path.GetDirectoryName(_configFilePath), "DocGen");
                Directory.CreateDirectory(newPath);
                _settings = new Settings { Paths = new string[] { newPath } };
                XmlClassify.SaveObjectToXmlFile(_settings, _configFilePath);
            }
        }

        private List<documentable> getDocList()
        {
            var list = new List<documentable>();
            foreach (var path in _paths)
            {
                foreach (var file in new DirectoryInfo(path).GetFiles("*.dll"))
                {
                    // .GetFiles("*.dll") finds all files whose extension begins with .dll, i.e. it includes files like *.dll2. This is for backward-compatibility with 8.3 filenames. Filter these out.
                    if (!file.Name.EndsWith(".dll"))
                        continue;
                    var xmlFile = Path.Combine(file.DirectoryName, Path.GetFileNameWithoutExtension(file.Name) + ".docs.xml");
                    if (!File.Exists(xmlFile))
                        continue;
                    FileInfo xmlFileInfo = new FileInfo(xmlFile);
                    list.Add(new documentable { DllPath = file.FullName, DllLastChange = file.LastWriteTimeUtc, XmlPath = xmlFileInfo.FullName, XmlLastChange = xmlFileInfo.LastWriteTimeUtc });
                }
            }
            return list;
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
