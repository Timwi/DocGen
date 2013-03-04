using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Servers;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace RT.DocGen
{
    /// <summary>
    /// Provides an HTTP request handler that generates web pages from C# XML documentation.
    /// </summary>
    public class DocumentationGenerator
    {
        private class namespaceInfo
        {
            public SortedDictionary<string, typeInfo> Types;
        }

        private class typeInfo
        {
            public Type Type;
            public XElement Documentation;
            public SortedDictionary<string, memberInfo> Members;

            internal string GetTypeLetters()
            {
                if (Type.IsInterface)
                    return "In";
                if (Type.IsEnum)
                    return "En";
                if (typeof(Delegate).IsAssignableFrom(Type))
                    return "De";
                if (Type.IsValueType)
                    return "St";
                return "Cl";
            }

            internal string GetTypeCssClass()
            {
                if (Type.IsInterface)
                    return "Interface";
                if (Type.IsEnum)
                    return "Enum";
                if (typeof(Delegate).IsAssignableFrom(Type))
                    return "Delegate";
                if (Type.IsValueType)
                    return "Struct";
                return "Class";
            }
        }

        private class memberInfo
        {
            public MemberInfo Member;
            public XElement Documentation;
        }

        private class docGenSession : FileSession
        {
            public string Username;
            public override void CleanUp(HttpResponse response)
            {
                SessionModified = true;
                base.CleanUp(response);
            }
            public void SetUsername(string username)
            {
                if (username == null)
                    Delete = true;
                else
                    Username = username;
            }
        }

        private SortedDictionary<string, namespaceInfo> _namespaces;
        private SortedDictionary<string, typeInfo> _types;
        private SortedDictionary<string, memberInfo> _members;
        private string _usernamePasswordFile;
        private List<string> _assembliesLoaded = new List<string>();
        public List<string> AssembliesLoaded { get { return _assembliesLoaded; } }
        private List<Tuple<string, string>> _assemblyLoadErrors = new List<Tuple<string, string>>();
        public List<Tuple<string, string>> AssemblyLoadErrors { get { return _assemblyLoadErrors; } }

        private static string _css = @"
body {
    background: #eef;
    font-family: ""Candara"", ""Segoe UI"", ""Verdana"", sans-serif;
    font-size: 11pt;
    margin-bottom: 10em;
}

td {
    vertical-align: top;
}
td.left {
    padding-right: 1.5em;
}
td.right {
    width: 100%;
}

.boxy {
    border: 2px solid #666;
    border-top-color: #ccc;
    border-left-color: #aaa;
    border-right-color: #666;
    border-bottom-color: #444;
    border-radius: 10px;
    background: white;
    box-shadow: 3px 3px 3px #ccc;
    margin-bottom: 1em;
    padding: .3em 1em;
}

.links, .auth {
    font-variant: small-caps;
    text-align: center;
}

.tree ul {
    padding: 0;
}

.tree li {
    list-style-type: none;
    text-indent: -3em;
    padding-left: 3em;
}

.tree li div.namespace:before {
    content: '{ }';
    font-family: ""Verdana"", sans-serif;
    font-weight: bold;
    font-size: 8pt;
}

.indent {
    margin-left: 2em;
}

.Class:before, .Struct:before, .Enum:before, .Interface:before, .Delegate:before,
    .Constructor:before, .Method:before, .Property:before, .Event:before, .Field:before,
    .tree li div.namespace:before {
    width: 23px;
    display: inline-block;
    text-align: center;
    border-radius: 5px;
    margin-right: .5em;
    margin-top: 1px;
    text-indent: 0;
    padding-left: 0;
    color: black;
}

div.Class:before       { content: 'Cl'; background: #4df; }
div.Struct:before      { content: 'St'; background: #f9f; }
div.Enum:before        { content: 'En'; background: #4f8; }
div.Interface:before   { content: 'In'; background: #f44; }
div.Delegate:before    { content: 'De'; background: #ff4; }
div.Constructor:before { content: 'C'; background: #bfb; }
div.Method:before      { content: 'M'; background: #cdf; }
div.Property:before    { content: 'P'; background: #fcf; }
div.Event:before       { content: 'E'; background: #faa; }
div.Field:before       { content: 'F'; background: #ee8; }

.legend p {
    font-variant: small-caps;
    text-align: center;
    margin: 0 -.7em;
    background: -moz-linear-gradient(#e8f0ff, #d0e0f8);
    background: linear-gradient(#e8f0ff, #d0e0f8);
    border-radius: 5px;
}

.content {
    padding: 0;
}

.innercontent {
    padding: 1em 2em 3em;
}

h1 {
    border-top-left-radius: 8px;
    border-top-right-radius: 8px;
    background: -moz-linear-gradient(#fff, #abe);
    background: linear-gradient(#fff, #abe);
    margin: 0;
    padding: .5em 1em;
    font-weight: bold;
    font-size: 24pt;
    border-bottom: 1px solid #888;
}

h1 .Method, h1 .Constructor {
    font-weight: normal;
    color: #668;
}

h1 .Method strong, h1 .Constructor strong {
    font-weight: bold;
    color: black;
}

h1 .namespace {
    color: #668;
    font-weight: normal;
}

h2 {
    font-weight: bold;
    font-size: 18pt;
    border-bottom: 1px solid #aad;
    margin: 1.5em 0 .8em;
    background: -moz-linear-gradient(left, #dde 0%, #fff 15%);
    background: linear-gradient(left, #dde 0%, #fff 20%);
    border-top-left-radius: 10px;
    padding: 0 1em;
}

pre, code {
    font-family: ""Candara"", ""Segoe UI"", ""Verdana"", sans-serif;
}

pre {
    background: #eee;
    padding: .7em 1.5em;
    border-left: 3px solid #8ad;
    border-top-right-radius: 20px;
    border-bottom-right-radius: 20px;
}

code {
    color: #468;
    font-weight: bold;
    padding: 0 .2em;
}

.paramtype { text-align: right; }

table.doclist {
    border-collapse: collapse;
}
table.doclist td {
    border: 1px solid #cde;
    padding: .5em 1em;
}
table.doclist td .withicon {
    text-indent: -3em;
    padding-left: 3em;
}

ul.extra {
    font-size: 8pt;
    opacity: .4;
    margin: .2em 0 0 3em;
    padding: 0;
    list-style-type: none;
}
ul.extra li {
    margin: 0;
}

.sep {
    padding: 0 1em;
    font-family: ""Verdana"", sans-serif;
}
strong.sep {
    padding-left: 0;
}

.missing {
    background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAYAAACNiR0NAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAC8SURBVDhPY2AYtiBBgX92vDz/zXh5vkMUexJkGNhAOf6sBAWB//FyfEYUGQpyWZaGADdVXBirwKUK8iYQ20Ncx59FmeuA3kuQF6gEuq6LKuEHCq9ERYFokGEgmiLXgTSDvAgylCquAxsI9CrIZSBvU+w6mIGgJEMVw0CGAGN2LdVcFy0rJAFKe1RzHSTtUdG7VHMZ1Q2C5gxgycJ/M06B35ciCyAlCv9scNqjRt4FuQiEQckFlKgpch05mgHc9Dht+XE3awAAAABJRU5ErkJggg==) no-repeat right center;
}

.highlighted {
    background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABQAAAAUCAYAAACNiR0NAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAACDSURBVDhPzdPNDYAgDIZhNnEU3MRRcBJX0UlcRduEJv4V2g8OkpAeSJ5weBvCn09M69DlfwRN47ztNFMTKBBhB18YfEIwqEFusAaZQStUBb2QCqLQC+QguSN5QOctm4wuKKZ22AIXw0Zg06Z4YBMoy26BXaAFhsAS3AR+wV3AK0xgPAFi473SDF1CWAAAAABJRU5ErkJggg==) no-repeat right center;
}

.warning {
    font-size: 14pt;
    margin: 2em 2em;
}

span.parameter, span.typeparameter, span.member { white-space: nowrap; }
h1 span.parameter, h1 span.typeparameter, h1 span.member { white-space: normal; }
";

        private class memberComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                var typeX = x[0];
                var typeY = y[0];
                if (typeX == 'M' && x.Contains(".#ctor")) typeX = 'C';
                if (typeY == 'M' && y.Contains(".#ctor")) typeY = 'C';

                if (typeX == typeY)
                    return x.CompareTo(y);

                foreach (var m in "CMEPF")
                    if (typeX == m) return -1;
                    else if (typeY == m) return 1;

                return 0;
            }
        }

        /// <summary>
        /// Initialises a <see cref="DocumentationGenerator"/> instance by searching the given paths for XML and DLL files.
        /// All pairs of matching <c>*.dll</c> and <c>*.xml</c> files are considered for documentation. The classes are extracted
        /// from the DLLs and grouped by namespaces.
        /// </summary>
        /// <param name="paths">Paths containing DLL and XML files.</param>
        /// <param name="usernamePasswordFile">Path to a file containing usernames and password hashes. If null, access is completely unrestricted.</param>
        /// <param name="copyDllFilesTo">Path to copy DLL files to prior to loading them into memory. If null, original DLLs are loaded.</param>
        public DocumentationGenerator(string[] paths, string usernamePasswordFile = null, string copyDllFilesTo = null)
        {
            _usernamePasswordFile = usernamePasswordFile;
            _namespaces = new SortedDictionary<string, namespaceInfo>();
            _types = new SortedDictionary<string, typeInfo>();
            _members = new SortedDictionary<string, memberInfo>();

            foreach (var path in paths)
            {
                // We need to copy all the DLLs files, even those that have no documentation, because they might be a dependency of ones of those that do
                var dllFiles = new DirectoryInfo(path).GetFiles("*.dll");
                if (copyDllFilesTo != null)
                    foreach (var dllFile in dllFiles)
                        File.Copy(dllFile.FullName, Path.Combine(copyDllFilesTo, dllFile.Name));

                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    var actualAssemblyName = new AssemblyName(e.Name).Name;
                    var prospectiveAssemblyPath = Path.Combine(copyDllFilesTo == null ? path : copyDllFilesTo, actualAssemblyName + ".dll");
                    if (File.Exists(prospectiveAssemblyPath))
                        return Assembly.LoadFrom(prospectiveAssemblyPath);
                    prospectiveAssemblyPath = Path.Combine(copyDllFilesTo == null ? path : copyDllFilesTo, actualAssemblyName + ".exe");
                    if (File.Exists(prospectiveAssemblyPath))
                        return Assembly.LoadFrom(prospectiveAssemblyPath);
                    return null;
                };

                foreach (var f in dllFiles.Where(f => File.Exists(f.FullName.Remove(f.FullName.Length - 3) + "xml")))
                {
                    string loadFromFile = copyDllFilesTo != null ? Path.Combine(copyDllFilesTo, f.Name) : f.FullName;
                    try
                    {
                        var docsFile = f.FullName.Remove(f.FullName.Length - 3) + "xml";
                        Assembly a = Assembly.LoadFile(loadFromFile);
                        XElement e = XElement.Load(docsFile);

                        foreach (var t in a.GetExportedTypes().Where(t => shouldTypeBeDisplayed(t)))
                        {
                            var typeFullName = getTypeFullName(t);
                            XElement doc = e.Element("members").Elements("member").FirstOrDefault(n => n.Attribute("name").Value == typeFullName);

                            if (!_namespaces.ContainsKey(t.Namespace))
                                _namespaces[t.Namespace] = new namespaceInfo { Types = new SortedDictionary<string, typeInfo>() };

                            var typeinfo = new typeInfo { Type = t, Documentation = doc, Members = new SortedDictionary<string, memberInfo>(new memberComparer()) };

                            foreach (var mem in t.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                                .Where(m => m.DeclaringType == t && shouldMemberBeDisplayed(m)))
                            {
                                var dcmn = documentationCompatibleMemberName(mem);
                                XElement mdoc = e.Element("members").Elements("member").FirstOrDefault(n => n.Attribute("name").Value == dcmn);

                                // Special case: if it's an automatically-generated public default constructor without documentation, auto-generate documentation for it
                                if (mem is ConstructorInfo && mdoc == null)
                                {
                                    var c = (ConstructorInfo) mem;
                                    if (c.IsPublic && c.GetParameters().Length == 0)
                                        mdoc = new XElement("member", new XAttribute("name", dcmn), new XElement("summary", "Creates a new instance of ", new XElement("see", new XAttribute("cref", typeFullName)), "."));
                                }

                                var memDoc = new memberInfo { Member = mem, Documentation = mdoc };
                                typeinfo.Members[dcmn] = memDoc;
                                _members[dcmn] = memDoc;
                            }

                            _namespaces[t.Namespace].Types[typeFullName] = typeinfo;
                            _types[typeFullName] = typeinfo;
                        }
                    }
                    catch (Exception exc)
                    {
                        _assemblyLoadErrors.Add(Tuple.Create(loadFromFile, exc.Message + " (" + exc.GetType().FullName + ")"));
                        continue;
                    }
                    _assembliesLoaded.Add(loadFromFile);
                }
            }
        }

        private string getTypeFullName(Type t)
        {
            return "T:" + (t.IsGenericType ? t.GetGenericTypeDefinition() : t).FullName.TrimEnd('&').Replace("+", ".");
        }

        private Authenticator _authenticator;
        private UrlPathResolver _resolver;

        /// <summary>Provides the HTTP request handler for the documentation.</summary>
        public HttpResponse Handler(HttpRequest request)
        {
            if (_authenticator == null && _usernamePasswordFile != null)
                _authenticator = new Authenticator(_usernamePasswordFile, request.Url.WithPathOnly("/").ToHref(), "the documentation");

            if (_resolver == null)
            {
                _resolver = new UrlPathResolver();
                _resolver.Add(new UrlPathHook(path: "/css", specificPath: true, handler: req => HttpResponse.Create(_css, "text/css; charset=utf-8")));
                _resolver.Add(new UrlPathHook(path: "/q", handler: quickUrl));
                if (_usernamePasswordFile != null)
                    _resolver.Add(new UrlPathHook(path: "/auth", handler: req => Session.Enable<docGenSession>(req, session => _authenticator.Handle(req, session.Username, session.SetUsername))));
                _resolver.Add(new UrlPathHook(handler: handle));
            }

            return _resolver.Handle(request);
        }

        private HttpResponse handle(HttpRequest req)
        {
            if (req.Url.Path == "")
                return HttpResponse.Redirect(req.Url.WithPath("/"));

            return Session.Enable<docGenSession>(req, session =>
            {
                if (session.Username == null && _usernamePasswordFile != null)
                    return HttpResponse.Redirect(req.Url.WithPathOnly("/auth/login").WithQuery("returnto", req.Url.ToHref()));

                string ns = null;
                Type type = null;
                MemberInfo member = null;
                string token = req.Url.Path.Substring(1).UrlUnescape();

                HttpStatusCode status = HttpStatusCode._200_OK;
                string title;
                object content;

                if (req.Url.Path == "/all:types")
                {
                    title = "All types";
                    content = generateAllTypes(req);
                }
                else if (req.Url.Path == "/all:members")
                {
                    title = "All members";
                    content = generateAllMembers(req);
                }
                else if (_namespaces.ContainsKey(token))
                {
                    ns = token;
                    title = "Namespace: " + ns;
                    content = generateNamespaceDocumentation(ns, req);
                }
                else if (_types.ContainsKey(token))
                {
                    type = _types[token].Type;
                    ns = type.Namespace;
                    title = type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : typeof(Delegate).IsAssignableFrom(type) ? "Delegate: " : "Class: ";
                    title += stringSoup(friendlyTypeName(type, includeOuterTypes: true));
                    content = generateTypeDocumentation(req, _types[token].Type, _types[token].Documentation);
                }
                else if (_members.ContainsKey(token))
                {
                    member = _members[token].Member;
                    type = member.DeclaringType;
                    ns = type.Namespace;
                    var isstatic = isStatic(member);
                    title = member.MemberType == MemberTypes.Constructor ? "Constructor: " :
                                member.MemberType == MemberTypes.Event ? "Event: " :
                                member.MemberType == MemberTypes.Field && isstatic ? "Static field: " :
                                member.MemberType == MemberTypes.Field ? "Field: " :
                                member.MemberType == MemberTypes.Method && isstatic ? "Static method: " :
                                member.MemberType == MemberTypes.Method ? "Method: " :
                                member.MemberType == MemberTypes.Property && isstatic ? "Static property: " :
                                member.MemberType == MemberTypes.Property ? "Property: " : "Member: ";
                    title += member.MemberType == MemberTypes.Constructor ? stringSoup(friendlyTypeName(type, includeOuterTypes: true)) : member.Name;
                    content = generateMemberDocumentation(req, _members[token].Member, _members[token].Documentation);
                }
                else if (req.Url.Path == "/")
                {
                    title = null;
                    content = new object[] { new H1("Welcome"), new DIV { class_ = "warning" }._("Select an item from the list on the left.") };
                }
                else
                {
                    title = "Not found";
                    content = new object[] { new H1("Not found"), new DIV { class_ = "warning" }._("This item is not documented.") };
                    status = HttpStatusCode._404_NotFound;
                }

                if (title != null && title.Length > 0)
                    title += " — ";
                title += "XML documentation";

                var html = new HTML(
                    new HEAD(
                        new TITLE(title),
                        new LINK { href = req.Url.WithPathOnly("/css").ToHref(), rel = "stylesheet", type = "text/css" }
                    ),
                    new BODY(
                        new TABLE { class_ = "layout" }._(
                            new TR(
                                new TD { class_ = "left" }._(
                                    new DIV { class_ = "boxy links" }._(
                                        new A("All types") { href = req.Url.WithPathOnly("/all:types").ToHref(), accesskey = "t", title = "Show all types [Alt-T]" }, new SPAN { class_ = "sep" }._("|"),
                                        new A("All members") { href = req.Url.WithPathOnly("/all:members").ToHref(), accesskey = "m", title = "Show all members [Alt-M]" }
                                    ),
                                    new DIV { class_ = "boxy tree" }._(
                                        new UL(_namespaces.Select(nkvp => new LI(
                                            new DIV { class_ = "namespace" + (nkvp.Key == ns && type == null ? " highlighted" : null) }._(
                                                new A { href = req.Url.WithPathOnly("/" + nkvp.Key.UrlEscape()).ToHref() }._(nkvp.Key)
                                            ),
                                            ns == null || ns != nkvp.Key ? null :
                                                nkvp.Value.Types.Where(tkvp => !tkvp.Value.Type.IsNested).Select(tkvp => generateTypeBullet(tkvp.Key, member ?? type, req))
                                        )))
                                    ),
                                    new DIV { class_ = "boxy legend" }._(
                                        new P("Legend"),
                                        new TABLE { style = "width:100%" }._(
                                            new TR(
                                                new[] { new[] { "Class", "Struct", "Enum", "Interface", "Delegate" }, new[] { "Constructor", "Method", "Property", "Event", "Field" } }
                                                    .Select(arr => new TD(arr.Select(x => new DIV { class_ = x }._(x))))
                                            )
                                        )
                                    ),
                                    _usernamePasswordFile == null ? null : new DIV { class_ = "boxy auth" }._(
                                        new A("Log out") { href = req.Url.WithPathOnly("/auth/logout").ToHref() }, new SPAN { class_ = "sep" }._("|"),
                                        new A("Change password") { href = req.Url.WithPathOnly("/auth/changepassword").WithQuery("returnto", req.Url.ToHref()).ToHref() }
                                    )
                                ),
                                new TD { class_ = "right" }._(new DIV { class_ = "boxy content" }._(content))
                            )
                        )
                    )
                );

                return HttpResponse.Html(html, status);
            });
        }

        private IEnumerable<object> generateAllTypes(HttpRequest req)
        {
            yield return new H1("All types");
            yield return new DIV { class_ = "innercontent" }._(generateAll(
                _types,
                k => typeof(Delegate).IsAssignableFrom(k.Value.Type) ? 5 : k.Value.Type.IsInterface ? 4 : k.Value.Type.IsEnum ? 3 : k.Value.Type.IsValueType ? 2 : (k.Value.Type.IsAbstract && k.Value.Type.IsSealed) ? 0 : 1,
                new string[] { "Static classes", "Classes", "Structs", "Enums", "Interfaces", "Delegates" },
                "No types are defined.",
                k => stringSoup(friendlyTypeName(k.Value.Type)),
                k => friendlyTypeName(k.Value.Type, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true, modifiers: true, variance: true)
            ));
        }

        private IEnumerable<object> generateAllMembers(HttpRequest req)
        {
            yield return new H1("All members");
            yield return new DIV { class_ = "innercontent" }._(generateAll(
                _members.Where(k => k.Value.Member.MemberType != MemberTypes.NestedType),
                k =>
                    k.Value.Member.MemberType == MemberTypes.Constructor ? 0 :
                    k.Value.Member.MemberType == MemberTypes.Method ? 1 :
                    k.Value.Member.MemberType == MemberTypes.Property ? 2 :
                    k.Value.Member.MemberType == MemberTypes.Event ? 3 : 4,
                new string[] { "Constructors", "Methods", "Properties", "Events", "Fields" },
                "No members are defined.",
                k => stringSoup(friendlyMemberName(k.Value.Member, parameterTypes: true, stringOnly: true)),
                k => friendlyMemberName(k.Value.Member, parameterTypes: true, url: req.Url.WithPathOnly("/" + k.Key.UrlEscape()).ToHref(), baseUrl: req.Url.WithPathOnly("").ToHref())
            ));
        }

        private IEnumerable<object> generateAll<TSource>(IEnumerable<TSource> source, Func<TSource, int> categorize,
            string[] humanReadable, string noneDefined, Func<TSource, string> itemSortKey, Func<TSource, object> html)
        {
            var categories = source.Select(categorize).Distinct().Order().ToArray();
            if (categories.Length == 0)
            {
                yield return new DIV { class_ = "warning" }._(noneDefined);
                yield break;
            }

            yield return new DIV(
                new STRONG { class_ = "sep" }._("Jump to:"),
                categories.Select(cat => new A { href = "#cat" + cat }._(humanReadable[cat])).InsertBetween<object>(new SPAN { class_ = "sep" }._("|"))
            );

            foreach (var group in source.GroupBy(categorize).OrderBy(gr => gr.Key))
            {
                yield return new H2(humanReadable[group.Key]) { id = "cat" + group.Key };
                yield return group.OrderBy(itemSortKey).Select(html).InsertBetween(" | ");
            }
        }

        private IEnumerable<object> friendlyTypeName(Type t, bool includeNamespaces = false, bool includeOuterTypes = false, bool variance = false, string baseUrl = null, bool inclRef = false, bool span = false, bool modifiers = false, bool namespaceSpan = false)
        {
            if (span)
            {
                yield return new SPAN { class_ = "type", title = stringSoup(friendlyTypeName(t, includeNamespaces: true, includeOuterTypes: true)) }._(
                    friendlyTypeName(t, includeNamespaces, includeOuterTypes, variance, baseUrl, inclRef, span: false, namespaceSpan: true)
                );
                yield break;
            }

            if (t.IsByRef)
            {
                if (inclRef)
                    yield return "ref ";
                t = t.GetElementType();
            }

            if (t.IsArray)
            {
                yield return friendlyTypeName(t.GetElementType(), includeNamespaces, includeOuterTypes, variance, span: span);
                yield return "[]";
                yield break;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                yield return friendlyTypeName(t.GetGenericArguments()[0], includeNamespaces, includeOuterTypes, variance, span: span);
                yield return "?";
                yield break;
            }

            // Use the C# identifier for built-in types
            if (t == typeof(int)) yield return "int";
            else if (t == typeof(uint)) yield return "uint";
            else if (t == typeof(long)) yield return "long";
            else if (t == typeof(ulong)) yield return "ulong";
            else if (t == typeof(short)) yield return "short";
            else if (t == typeof(ushort)) yield return "ushort";
            else if (t == typeof(byte)) yield return "byte";
            else if (t == typeof(sbyte)) yield return "sbyte";
            else if (t == typeof(string)) yield return "string";
            else if (t == typeof(char)) yield return "char";
            else if (t == typeof(float)) yield return "float";
            else if (t == typeof(double)) yield return "double";
            else if (t == typeof(decimal)) yield return "decimal";
            else if (t == typeof(bool)) yield return "bool";
            else if (t == typeof(void)) yield return "void";
            else if (t == typeof(object)) yield return "object";
            else
            {
                if (t.IsGenericParameter && variance)
                {
                    if (t.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Contravariant))
                        yield return "in ";
                    else if (t.GenericParameterAttributes.HasFlag(GenericParameterAttributes.Covariant))
                        yield return "out ";
                }
                if (modifiers)
                {
                    if (t.IsAbstract && t.IsSealed)
                        yield return "static ";
                    else if (t.IsAbstract)
                        yield return "abstract ";
                    else if (t.IsSealed)
                        yield return "sealed ";
                }
                if (includeOuterTypes && t.IsNested && !t.IsGenericParameter)
                {
                    yield return friendlyTypeName(t.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance, baseUrl: baseUrl, span: span);
                    yield return ".";
                }
                else if (includeNamespaces && !t.IsGenericParameter)
                {
                    var arr = new object[] { t.Namespace, "." };
                    yield return namespaceSpan ? new SPAN { class_ = "namespace" }._(arr) : (object) arr;
                }

                // Determine whether this type has its own generic type parameters.
                // This is different from being a generic type: a nested type of a generic type is automatically a generic type too, even though it doesn't have generic parameters of its own.
                var hasGenericTypeParameters = t.Name.Contains('`');

                string ret = t.IsGenericParameter ? t.Name : hasGenericTypeParameters ? t.Name.Remove(t.Name.IndexOf('`')) : t.Name;
                if (baseUrl != null && !t.IsGenericParameter && _types.ContainsKey(getTypeFullName(t)))
                    yield return new A(ret) { href = baseUrl + "/" + getTypeFullName(t).UrlEscape() };
                else
                    yield return ret;

                if (hasGenericTypeParameters)
                {
                    yield return "<";
                    bool first = true;
                    // Need to skip the generic type parameters already declared by the parent type
                    int skip = t.IsNested ? t.DeclaringType.GetGenericArguments().Length : 0;
                    foreach (var ga in t.GetGenericArguments().Skip(skip))
                    {
                        if (!first) yield return ", ";
                        first = false;
                        yield return friendlyTypeName(ga, includeNamespaces, includeOuterTypes, variance, baseUrl, inclRef, span);
                    }
                    yield return ">";
                }
            }
        }

        private object friendlyMemberName(MemberInfo m, bool returnType = false, bool containingType = false, bool parameterTypes = false, bool parameterNames = false, bool parameterDefaultValues = false, bool omitGenericTypeParameters = false, bool namespaces = false, bool variance = false, bool indent = false, string url = null, string baseUrl = null, bool stringOnly = false, bool modifiers = false)
        {
            if (m.MemberType == MemberTypes.NestedType)
                return friendlyTypeName((Type) m, includeNamespaces: namespaces, includeOuterTypes: true, variance: variance, baseUrl: baseUrl, span: !stringOnly, modifiers: modifiers);

            if (m.MemberType == MemberTypes.Constructor || m.MemberType == MemberTypes.Method)
            {
                var f = friendlyMethodName((MethodBase) m, returnType, containingType, parameterTypes, parameterNames, parameterDefaultValues, omitGenericTypeParameters, namespaces, variance, indent, url, baseUrl, stringOnly: stringOnly, modifiers: modifiers);
                return stringOnly ? (object) f : new SPAN { class_ = m.MemberType.ToString(), title = stringSoup(friendlyMemberName(m, true, true, true, true, true, stringOnly: true, modifiers: true)) }._(f);
            }

            var arr = Ut.NewArray<object>(
                returnType && m.MemberType == MemberTypes.Property ? new object[] { friendlyTypeName(((PropertyInfo) m).PropertyType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly), " " } :
                returnType && m.MemberType == MemberTypes.Event ? new object[] { friendlyTypeName(((EventInfo) m).EventHandlerType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly), " " } :
                returnType && m.MemberType == MemberTypes.Field ? new object[] { friendlyTypeName(((FieldInfo) m).FieldType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly), " " } : null,
                containingType ? friendlyTypeName(m.DeclaringType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly) : null,
                containingType ? "." : null,
                m.MemberType == MemberTypes.Property
                    ? (object) friendlyPropertyName((PropertyInfo) m, parameterTypes, parameterNames, namespaces, indent, url, baseUrl, stringOnly)
                    : stringOnly ? (object) m.Name : new STRONG(url == null ? (object) m.Name : new A { href = url }._(m.Name))
            );
            return stringOnly ? (object) stringSoup(arr) : new SPAN { class_ = m.MemberType.ToString(), title = stringSoup(friendlyMemberName(m, true, true, true, true, true, stringOnly: true)) }._(arr);
        }

        private IEnumerable<object> friendlyMethodName(MethodBase m, bool returnType = false, bool containingType = false, bool parameterTypes = false, bool parameterNames = false, bool parameterDefaultValues = false, bool omitGenericTypeParameters = false, bool includeNamespaces = false, bool variance = false, bool indent = false, string url = null, string baseUrl = null, bool isDelegate = false, bool stringOnly = false, bool modifiers = false)
        {
            if (isDelegate)
                yield return "delegate ";
            if (modifiers)
            {
                if (m.IsStatic)
                    yield return "static ";
                if (m.IsVirtual)
                {
                    if (m.IsAbstract)
                    {
                        if (!m.DeclaringType.IsInterface)
                            yield return "abstract ";
                    }
                    else if (m is MethodInfo && ((MethodInfo) m).GetBaseDefinition() != m)
                    {
                        if (m.IsFinal)
                            yield return "sealed ";
                        yield return "override ";
                    }
                    else if (!m.IsFinal)
                        yield return "virtual ";
                }
            }
            if (returnType && m.MemberType != MemberTypes.Constructor)
            {
                var meth = (MethodInfo) m;
                // For the cast operators, omit the return type because they later become part of the operator name
                if (meth.Name != "op_Implicit" && meth.Name != "op_Explicit")
                {
                    yield return friendlyTypeName(meth.ReturnType, includeNamespaces: includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly);
                    yield return " ";
                }
            }
            if ((m.MemberType == MemberTypes.Constructor || isDelegate) && url != null)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: isDelegate, span: false) : new STRONG(new A { href = url }._(friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: isDelegate, span: true)));
            else if (isDelegate)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance) : new STRONG(friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance, baseUrl: baseUrl, span: true));
            else if (containingType)
                yield return friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly);
            else if (m.MemberType == MemberTypes.Constructor)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces) : new STRONG(friendlyTypeName(m.DeclaringType, includeNamespaces, baseUrl: baseUrl, span: true));
            if (!stringOnly && !indent && (m.IsGenericMethod || (parameterNames && m.GetParameters().Any()))) yield return new WBR();
            if (m.MemberType != MemberTypes.Constructor && !isDelegate)
            {
                if (containingType) yield return ".";
                object f = cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, includeOuterTypes: true, span: !stringOnly));
                if (url != null) f = new A(f) { href = url };
                if (!stringOnly) f = new STRONG(f);
                yield return f;
            }
            if (m.IsGenericMethod)
            {
                if (!stringOnly && !indent) yield return new WBR();
                yield return omitGenericTypeParameters ? "<>" : "<" + m.GetGenericArguments().Select(ga => ga.Name).JoinString(", ") + ">";
            }
            if (parameterTypes || parameterNames)
            {
                yield return indent && m.GetParameters().Any() ? "(\n    " : "(";
                if (!stringOnly && !indent && m.GetParameters().Any()) yield return new WBR();
                bool first = true;
                foreach (var p in m.GetParameters())
                {
                    if (!first) yield return indent ? ",\n    " : ", ";
                    first = false;
                    var f = Ut.NewArray<object>(
                        parameterTypes && p.IsOut ? "out " : null,
                        parameterTypes && p.IsDefined<ParamArrayAttribute>() ? "params " : null,
                        parameterTypes ? friendlyTypeName(p.ParameterType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, inclRef: !p.IsOut, span: !stringOnly) : null,
                        parameterTypes && parameterNames ? " " : null,
                        parameterNames ? (stringOnly || !parameterTypes ? (object) p.Name : new STRONG(p.Name)) : null,
                        parameterDefaultValues && p.Attributes.HasFlag(ParameterAttributes.HasDefault) ? friendlyDefaultValue(p.DefaultValue, baseUrl, stringOnly) : null
                    );
                    yield return stringOnly ? (object) stringSoup(f) : new SPAN { class_ = "parameter" }._(f);
                }
                yield return indent && m.GetParameters().Any() ? "\n)" : ")";
            }
        }

        private IEnumerable<object> friendlyDefaultValue(object val, string baseUrl, bool stringOnly = false)
        {
            yield return " = ";
            if (val == null)
            {
                yield return "null";
                yield break;
            }
            var t = val.GetType();
            if (t.IsEnum && _types.ContainsKey(getTypeFullName(t)))
            {
                var info = _types[getTypeFullName(t)];
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public))
                    if (f.GetValue(null).Equals(val) && info.Members.ContainsKey(documentationCompatibleMemberName(f)))
                    {
                        yield return friendlyMemberName(f, containingType: true, url: baseUrl == null ? null : baseUrl + "/" + documentationCompatibleMemberName(f).UrlEscape(), baseUrl: baseUrl, stringOnly: stringOnly);
                        yield break;
                    }
                yield return "0x" + Convert.ToInt64(val).ToString("x");
            }
            else if (t == typeof(string))
                yield return "\"" + val.ToString().CLiteralEscape() + "\"";
            else if (t == typeof(bool))
                yield return val.ToString().ToLower();
            else
                yield return val.ToString();
        }

        private object cSharpCompatibleMethodName(string methodName, object returnType)
        {
            switch (methodName)
            {
                case "op_Implicit": return new object[] { "implicit operator ", returnType };
                case "op_Explicit": return new object[] { "explicit operator ", returnType };

                case "op_UnaryPlus":
                case "op_Addition": return "operator+";
                case "op_UnaryNegation":
                case "op_Subtraction": return "operator-";

                case "op_LogicalNot": return "operator!";
                case "op_OnesComplement": return "operator~";
                case "op_Increment": return "operator++";
                case "op_Decrement": return "operator--";
                case "op_True": return "operator true";
                case "op_False": return "operator false";

                case "op_Multiply": return "operator*";
                case "op_Division": return "operator/";
                case "op_Modulus": return "operator%";
                case "op_BitwiseAnd": return "operator&";
                case "op_BitwiseOr": return "operator|";
                case "op_ExclusiveOr": return "operator^";
                case "op_LeftShift": return "operator<<";
                case "op_RightShift": return "operator>>";

                case "op_Equality": return "operator==";
                case "op_Inequality": return "operator!=";
                case "op_LessThan": return "operator<";
                case "op_GreaterThan": return "operator>";
                case "op_LessThanOrEqual": return "operator<=";
                case "op_GreaterThanOrEqual": return "operator>=";

                default:
                    return methodName;
            }
        }

        private IEnumerable<object> friendlyPropertyName(PropertyInfo property, bool parameterTypes, bool parameterNames, bool includeNamespaces, bool indent, string url, string baseUrl, bool stringOnly)
        {
            var prms = property.GetIndexParameters();
            if (prms.Length > 0)
            {
                yield return stringOnly ? (object) "this" : new STRONG(url == null ? (object) "this" : new A { href = url }._("this"));
                yield return indent ? "[\n    " : "[";
                if (!indent && !stringOnly) yield return new WBR();
                bool first = true;
                foreach (var p in prms)
                {
                    if (!first) yield return indent ? ",\n    " : ", ";
                    first = false;
                    var arr = Ut.NewArray<object>(
                        parameterTypes && p.IsOut ? "out " : null,
                        parameterTypes ? friendlyTypeName(p.ParameterType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, inclRef: !p.IsOut, span: !stringOnly) : null,
                        parameterTypes && parameterNames ? " " : null,
                        parameterNames ? (stringOnly ? (object) p.Name : new STRONG(p.Name)) : null
                    );
                    yield return stringOnly ? (object) arr : new SPAN { class_ = "parameter" }._(arr);
                }
                yield return indent ? "\n]" : "]";
            }
            else
                yield return stringOnly ? (object) property.Name : new STRONG(url == null ? (object) property.Name : new A { href = url }._(property.Name));
        }

        private bool shouldMemberBeDisplayed(MemberInfo m)
        {
            if (m.ReflectedType.IsEnum && m.Name == "value__")
                return false;
            if (m.MemberType == MemberTypes.Constructor)
                return !(m as ConstructorInfo).IsPrivate;
            if (m.MemberType == MemberTypes.Method)
                return !(m as MethodInfo).IsPrivate && !isPropertyGetterOrSetter(m) && !isEventAdderOrRemover(m);
            if (m.MemberType == MemberTypes.Event)
                return ((m as EventInfo).GetAddMethod() != null && !(m as EventInfo).GetAddMethod().IsPrivate) ||
                    ((m as EventInfo).GetRemoveMethod() != null && !(m as EventInfo).GetRemoveMethod().IsPrivate);
            if (m.MemberType == MemberTypes.Property)
                return ((m as PropertyInfo).GetGetMethod() != null && !(m as PropertyInfo).GetGetMethod().IsPrivate) ||
                    ((m as PropertyInfo).GetSetMethod() != null && !(m as PropertyInfo).GetSetMethod().IsPrivate);
            if (m.MemberType == MemberTypes.Field)
                return !(m as FieldInfo).IsPrivate && !isEventField(m);
            if (m.MemberType == MemberTypes.NestedType)
                return shouldTypeBeDisplayed((Type) m);

            return false;
        }

        private bool shouldTypeBeDisplayed(Type t)
        {
            return !t.IsNested || !t.IsNestedPrivate;
        }

        private string documentationCompatibleMemberName(MemberInfo m)
        {
            StringBuilder sb = new StringBuilder();
            if (m.MemberType == MemberTypes.Method || m.MemberType == MemberTypes.Constructor)
            {
                MethodBase mi = m as MethodBase;
                sb.Append("M:");
                var declaringType = mi.DeclaringType;
                if (declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition)
                    declaringType = declaringType.GetGenericTypeDefinition();
                sb.Append(declaringType.FullName.Replace("+", "."));
                sb.Append(m.MemberType == MemberTypes.Method ? "." + mi.Name : ".#ctor");
                if (mi.IsGenericMethod)
                {
                    sb.Append("``");
                    sb.Append(mi.GetGenericArguments().Count());
                }
                appendParameterTypes(sb, mi.GetParameters(), mi.ReflectedType, mi as MethodInfo);
            }
            else if (m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Event)
            {
                sb.Append(m.MemberType == MemberTypes.Field ? "F:" : "E:");
                sb.Append(m.DeclaringType.FullName.Replace("+", "."));
                sb.Append(".");
                sb.Append(m.Name);
            }
            else if (m.MemberType == MemberTypes.Property)
            {
                var prop = (PropertyInfo) m;
                sb.Append("P:");
                sb.Append(prop.DeclaringType.FullName.Replace("+", "."));
                sb.Append(".");
                sb.Append(prop.Name);
                if (m.MemberType == MemberTypes.Property && prop.GetIndexParameters().Length > 0)
                    appendParameterTypes(sb, prop.GetIndexParameters(), prop.ReflectedType);
            }
            else if (m.MemberType == MemberTypes.NestedType)
            {
                sb.Append(getTypeFullName((Type) m));
            }
            else
            {
                sb.Append(m.ToString());
                sb.Append(" (Unknown member type: " + m.MemberType + ")");
            }

            // Special case: cast operators have their return type tacked onto the end because otherwise the signature wouldn't be unique
            if (m is MethodInfo && (m.Name == "op_Implicit" || m.Name == "op_Explicit"))
            {
                var b = (MethodInfo) m;
                sb.Append("~" + stringifyParameterType(b.ReturnType, b.ReflectedType, b));
            }
            return sb.ToString();
        }

        private void appendParameterTypes(StringBuilder sb, ParameterInfo[] parameters, Type type, MethodInfo method = null)
        {
            bool first = true;
            foreach (var param in parameters)
            {
                sb.Append(first ? "(" : ",");
                first = false;
                sb.Append(stringifyParameterType(param.ParameterType, type, method));
            }
            if (!first) sb.Append(")");
        }

        private string stringifyParameterType(Type parameterType, Type type, MethodInfo method)
        {
            if (parameterType.IsByRef)
                return stringifyParameterType(parameterType.GetElementType(), type, method) + "@";

            if (parameterType.IsArray)
                return stringifyParameterType(parameterType.GetElementType(), type, method) + "[]";

            if (!parameterType.IsGenericType && !parameterType.IsGenericParameter)
                return parameterType.FullName.Replace("+", ".");

            if (parameterType.IsGenericParameter)
            {
                int i = 0;
                if (method != null && method.IsGenericMethodDefinition)
                {
                    foreach (var p in method.GetGenericArguments())
                    {
                        if (p == parameterType)
                            return "``" + i;
                        i++;
                    }
                }
                if (type.IsGenericTypeDefinition)
                {
                    i = 0;
                    foreach (var p in type.GetGenericArguments())
                    {
                        if (p == parameterType)
                            return "`" + i;
                        i++;
                    }
                }
                throw new Exception("Parameter type is a generic type, but its generic argument is neither in the class nor method definition.");
            }

            if (parameterType.IsGenericType)
            {
                string fullName = parameterType.GetGenericTypeDefinition().FullName.Replace("+", ".");
                string constructName = "";
                Match m;
                IEnumerable<Type> genericArguments = parameterType.GetGenericArguments();
                while ((m = Regex.Match(fullName, @"`(\d+)")).Success)
                {
                    int num = int.Parse(m.Groups[1].Value);
                    constructName += fullName.Substring(0, m.Index) + "{" + genericArguments.Take(num).Select(g => stringifyParameterType(g, type, method)).JoinString(",") + "}";
                    fullName = fullName.Substring(m.Index + m.Length);
                    genericArguments = genericArguments.Skip(num);
                }
                return constructName + fullName;
            }

            throw new Exception("I totally don't know what to do with this parameter type.");
        }

        private bool isPropertyGetterOrSetter(MemberInfo member)
        {
            if (member.MemberType != MemberTypes.Method)
                return false;
            if (!member.Name.StartsWith("get_") && !member.Name.StartsWith("set_"))
                return false;
            string partName = member.Name.Substring(4);
            return member.DeclaringType.GetMembers().Any(m => m.MemberType == MemberTypes.Property && m.Name == partName);
        }

        private bool isEventAdderOrRemover(MemberInfo member)
        {
            if (member.MemberType != MemberTypes.Method)
                return false;
            if (!member.Name.StartsWith("add_") && !member.Name.StartsWith("remove_"))
                return false;
            string partName = member.Name.Substring(member.Name.StartsWith("add_") ? 4 : 7);
            return member.DeclaringType.GetMembers().Any(m => m.MemberType == MemberTypes.Event && m.Name == partName);
        }

        private bool isEventField(MemberInfo member)
        {
            if (member.MemberType != MemberTypes.Field)
                return false;
            return member.DeclaringType.GetMembers().Any(m => m.MemberType == MemberTypes.Event && m.Name == member.Name);
        }

        private bool isPublic(MemberInfo member)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Constructor:
                case MemberTypes.Method:
                    return ((MethodBase) member).IsPublic;

                case MemberTypes.Event:
                    return true;

                case MemberTypes.Field:
                    return ((FieldInfo) member).IsPublic;

                case MemberTypes.NestedType:
                    return ((Type) member).IsPublic || ((Type) member).IsNestedPublic;

                case MemberTypes.Property:
                    var property = (PropertyInfo) member;
                    var getter = property.GetGetMethod();
                    if (getter != null && getter.IsPublic)
                        return true;
                    var setter = property.GetSetMethod();
                    return setter != null && setter.IsPublic;
            }
            return false;
        }

        private bool isStatic(MemberInfo member)
        {
            if (member == null)
                return false;
            if (member.MemberType == MemberTypes.Field)
                return ((FieldInfo) member).IsStatic;
            if (member.MemberType == MemberTypes.Method)
                return ((MethodInfo) member).IsStatic;
            if (member.MemberType != MemberTypes.Property)
                return false;
            var property = (PropertyInfo) member;
            var getMethod = property.GetGetMethod();
            return getMethod == null ? property.GetSetMethod().IsStatic : getMethod.IsStatic;
        }

        private object generateTypeBullet(string typeFullName, MemberInfo selectedMember, HttpRequest req)
        {
            var typeinfo = _types[typeFullName];
            string cssClass = typeinfo.GetTypeCssClass();
            if (typeinfo.Documentation == null) cssClass += " missing";
            if (typeinfo.Type == selectedMember) cssClass += " highlighted";
            return new DIV { class_ = "type indent" }._(new DIV { class_ = cssClass }._(new A { href = req.Url.WithPathOnly("/" + typeFullName.UrlEscape()).ToHref() }._(friendlyTypeName(typeinfo.Type, span: true))),
                selectedMember != null && !typeof(Delegate).IsAssignableFrom(typeinfo.Type) && isMemberInside(selectedMember, typeinfo.Type)
                    ? typeinfo.Members.Where(mkvp => isPublic(mkvp.Value.Member)).Select(mkvp =>
                    {
                        if (mkvp.Value.Member.MemberType == MemberTypes.NestedType)
                            return generateTypeBullet(getTypeFullName((Type) mkvp.Value.Member), selectedMember, req);
                        string css = mkvp.Value.Member.MemberType.ToString() + " member indent";
                        if (mkvp.Value.Documentation == null) css += " missing";
                        if (mkvp.Value.Member == selectedMember) css += " highlighted";
                        return new DIV { class_ = css }._(new A { href = req.Url.WithPathOnly("/" + mkvp.Key.UrlEscape()).ToHref() }._(friendlyMemberName(mkvp.Value.Member, parameterNames: true, omitGenericTypeParameters: true)));
                    })
                    : null
            );
        }

        private bool isMemberInside(MemberInfo member, Type containingType)
        {
            return isNestedTypeOf(member is Type ? (Type) member : member.DeclaringType, containingType);
        }

        private bool isNestedTypeOf(Type nestedType, Type containingType)
        {
            if (nestedType == containingType)
                return true;
            if (!nestedType.IsNested)
                return false;
            return isNestedTypeOf(nestedType.DeclaringType, containingType);
        }

        private IEnumerable<object> generateNamespaceDocumentation(string namespaceName, HttpRequest req)
        {
            yield return new H1("Namespace: ", namespaceName);
            yield return new DIV { class_ = "innercontent" }._(
                new TABLE { class_ = "doclist" }._(
                    _namespaces[namespaceName].Types.Where(t => !t.Value.Type.IsNested).Select(kvp => new TR(
                        new TD { class_ = kvp.Value.Documentation == null ? "missing" : null }._(
                            new DIV { class_ = kvp.Value.GetTypeCssClass() }._(
                                new A { href = req.Url.WithPathOnly("/" + kvp.Key.UrlEscape()).ToHref() }._(friendlyTypeName(kvp.Value.Type, span: true))
                            )
                        ),
                        new TD(kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                            ? (object) new EM("This type is not documented.")
                            : interpretNodes(kvp.Value.Documentation.Element("summary").Nodes(), req))
                    ))
                )
            );
        }

        private IEnumerable<object> generateMemberDocumentation(HttpRequest req, MemberInfo member, XElement documentation)
        {
            bool isStatic =
                member.MemberType == MemberTypes.Field && (member as FieldInfo).IsStatic ||
                member.MemberType == MemberTypes.Method && (member as MethodInfo).IsStatic ||
                member.MemberType == MemberTypes.Property && (member as PropertyInfo).GetGetMethod().IsStatic;

            yield return new H1(
                member.MemberType == MemberTypes.Constructor ? "Constructor: " :
                member.MemberType == MemberTypes.Event ? "Event: " :
                member.MemberType == MemberTypes.Field ? (isStatic ? "Static field: " : "Field: ") :
                member.MemberType == MemberTypes.Method ? (isStatic ? "Static method: " : "Method: ") :
                member.MemberType == MemberTypes.Property ? (isStatic ? "Static property: " : "Property: ") : "Member: ",
                friendlyMemberName(member, returnType: true, parameterTypes: true)
            );

            yield return new DIV { class_ = "innercontent" }._(generateMemberDocumentationInner(req, member, documentation));
        }

        private IEnumerable<object> generateMemberDocumentationInner(HttpRequest req, MemberInfo member, XElement documentation)
        {
            yield return new UL(
                new LI("Declared in: ", friendlyTypeName(member.DeclaringType, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true)),
                formatMemberExtraInfoItems(req, member, true)
            );
            yield return new H2("Declaration");
            yield return new PRE(friendlyMemberName(member, returnType: true, parameterTypes: true, parameterNames: true, parameterDefaultValues: true, variance: true, indent: true, modifiers: true, baseUrl: req.Url.WithPathOnly("").ToHref()));

            if (documentation != null)
            {
                var summary = documentation.Element("summary");
                if (summary != null)
                {
                    yield return new H2("Summary");
                    yield return interpretNodes(summary.Nodes(), req);
                }
            }

            if (member.MemberType == MemberTypes.Constructor || member.MemberType == MemberTypes.Method)
            {
                var method = (MethodBase) member;
                if (method.GetParameters().Any())
                    yield return generateParameterDocumentation(req, method, documentation);
            }

            if (documentation != null)
            {
                var returns = documentation.Element("returns");
                if (returns != null)
                {
                    yield return new H2("Returns");
                    yield return interpretNodes(returns.Nodes(), req);
                }
                var exceptions = documentation.Elements("exception").Where(elem => elem.Attribute("cref") != null);
                if (exceptions.Any())
                {
                    yield return new H2("Exceptions");
                    yield return new UL(exceptions.Select(exc => new LI(interpretCref(exc.Attribute("cref").Value, req, true), new BLOCKQUOTE(interpretNodes(exc.Nodes(), req)))));
                }
                var remarks = documentation.Element("remarks");
                if (remarks != null)
                {
                    yield return new H2("Remarks");
                    yield return interpretNodes(remarks.Nodes(), req);
                }
                foreach (var example in documentation.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretNodes(example.Nodes(), req);
                }
                var seealsos = documentation.Elements("see").Concat(documentation.Elements("seealso"));
                if (seealsos.Any())
                {
                    yield return new H2("See also");
                    yield return new UL(seealsos.Select(sa => new LI(interpretCref(sa.Attribute("cref").Value, req, true))));
                }
            }

            if (member.MemberType == MemberTypes.Method && ((MethodBase) member).IsGenericMethod)
                yield return generateGenericTypeParameterTable(req, documentation, ((MethodBase) member).GetGenericArguments());
        }

        private IEnumerable<object> generateTypeDocumentation(HttpRequest req, Type type, XElement documentation)
        {
            bool isDelegate = typeof(Delegate).IsAssignableFrom(type);

            yield return new H1(
                type.IsNested
                    ? (isDelegate ? "Nested delegate: " : type.IsEnum ? "Nested enum: " : type.IsValueType ? "Nested struct: " : type.IsInterface ? "Nested interface: " : (type.IsAbstract && type.IsSealed) ? "Nested static class: " : type.IsAbstract ? "Nested abstract class: " : "Nested class: ")
                    : (isDelegate ? "Delegate: " : type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : (type.IsAbstract && type.IsSealed) ? "Static class: " : type.IsAbstract ? "Abstract class: " : "Class: "),
                friendlyTypeName(type, includeNamespaces: true, includeOuterTypes: true, variance: true, span: true)
            );

            yield return new DIV { class_ = "innercontent" }._(generateTypeDocumentationInner(req, type, documentation));
        }

        private IEnumerable<object> generateTypeDocumentationInner(HttpRequest req, Type type, XElement documentation)
        {
            bool isDelegate = typeof(Delegate).IsAssignableFrom(type);

            yield return new UL { class_ = "typeinfo" }._(
                new LI("Namespace: ", new A(type.Namespace) { class_ = "namespace", href = req.Url.WithPathOnly("/" + type.Namespace.UrlEscape()).ToHref() }),
                type.IsNested ? new LI("Declared in: ", friendlyTypeName(type.DeclaringType, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true)) : null,
                inheritsFrom(type, req),
                implementsInterfaces(type, req),
                type.IsInterface ? implementedBy(type, req) : derivedTypes(type, req)
            );

            MethodInfo m = null;
            if (isDelegate)
            {
                m = type.GetMethod("Invoke");
                yield return new H2("Declaration");
                yield return new PRE(friendlyMethodName(m, returnType: true, parameterTypes: true, parameterNames: true, includeNamespaces: true, variance: true, indent: true, baseUrl: req.Url.WithPathOnly("").ToHref(), isDelegate: true));
            }

            if (documentation == null)
                yield return new DIV(new EM("This type is not documented.")) { class_ = "warning" };
            else
            {
                var summary = documentation.Element("summary");
                if (summary != null)
                {
                    yield return new H2("Summary");
                    yield return interpretNodes(summary.Nodes(), req);
                }

                if (isDelegate)
                {
                    if (m.GetParameters().Any())
                        yield return generateParameterDocumentation(req, m, documentation);

                    var returns = documentation.Element("returns");
                    if (returns != null)
                    {
                        yield return new H2("Returns");
                        yield return interpretNodes(returns.Nodes(), req);
                    }
                }

                if (type.IsGenericType)
                {
                    var args = type.GetGenericArguments();
                    if (type.DeclaringType != null && type.DeclaringType.IsGenericType)
                        args = args.Skip(type.DeclaringType.GetGenericArguments().Length).ToArray();
                    yield return generateGenericTypeParameterTable(req, documentation, args);
                }

                var remarks = documentation.Element("remarks");
                if (remarks != null)
                {
                    yield return new H2("Remarks");
                    yield return interpretNodes(remarks.Nodes(), req);
                }
                foreach (var example in documentation.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretNodes(example.Nodes(), req);
                }
            }

            if (!isDelegate)
            {
                foreach (var gr in _types[getTypeFullName(type)].Members.Where(kvp => isPublic(kvp.Value.Member)).GroupBy(kvp => new
                {
                    MemberType = kvp.Value.Member.MemberType,
                    IsStatic = isStatic(kvp.Value.Member)
                }))
                {
                    var isEnumValues = gr.Key.MemberType == MemberTypes.Field && gr.Key.IsStatic && type.IsEnum;

                    yield return new H2(
                        gr.Key.MemberType == MemberTypes.Constructor ? "Constructors" :
                        gr.Key.MemberType == MemberTypes.Event ? "Events" :
                        isEnumValues ? "Enumeration values" :
                        gr.Key.MemberType == MemberTypes.Field && gr.Key.IsStatic ? "Static fields" :
                        gr.Key.MemberType == MemberTypes.Field ? "Instance fields" :
                        gr.Key.MemberType == MemberTypes.Method && gr.Key.IsStatic ? "Static methods" :
                        gr.Key.MemberType == MemberTypes.Method ? "Instance methods" :
                        gr.Key.MemberType == MemberTypes.Property && gr.Key.IsStatic ? "Static properties" :
                        gr.Key.MemberType == MemberTypes.Property ? "Instance properties" :
                        gr.Key.MemberType == MemberTypes.NestedType ? "Nested types" : "Additional members"
                    );

                    yield return new TABLE { class_ = "doclist" }._(Ut.Lambda(() =>
                    {
                        // Group members with the same documentation
                        return gr.GroupConsecutive((k1, k2) => sameSummary(k1.Value.Documentation, k2.Value.Documentation))
                            .SelectMany(acc => acc.Select((kvp, i) => new TR(
                                new TD(
                                    new DIV { class_ = "withicon " + (kvp.Value.Member.MemberType == MemberTypes.NestedType ? _types[kvp.Key].GetTypeCssClass() : kvp.Value.Member.MemberType.ToString()) }._(
                                        friendlyMemberName(kvp.Value.Member, returnType: !isEnumValues, parameterTypes: true, parameterNames: true, url: req.Url.WithPathOnly("/" + kvp.Key.UrlEscape()).ToHref(), baseUrl: req.Url.WithPathOnly("").ToHref())
                                    ),
                                    formatMemberExtraInfo(req, kvp.Value.Member, false)
                                ),
                                isEnumValues ? new TD("{0:X4}".Fmt(((FieldInfo) kvp.Value.Member).GetRawConstantValue())) : null,
                                i > 0 ? null : new TD { rowspan = acc.Count }._(kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                                    ? (object) new EM("This member is not documented.")
                                    : interpretNodes(kvp.Value.Documentation.Element("summary").Nodes(), req)
                                )
                            )));
                    }));
                }
            }
        }

        private IEnumerable<object> generateParameterDocumentation(HttpRequest req, MethodBase method, XElement methodDocumentation)
        {
            yield return new H2("Parameters");
            yield return new TABLE { class_ = "doclist" }._(
                method.GetParameters().Select(pi =>
                {
                    var parameterDocumentation = methodDocumentation == null ? null : methodDocumentation.Elements("param")
                        .FirstOrDefault(xe => xe.Attribute("name") != null && xe.Attribute("name").Value == pi.Name);
                    return new TR(
                        new TD { class_ = "paramtype" }._(
                            pi.IsOut ? "out " : null,
                            friendlyTypeName(pi.ParameterType, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), inclRef: !pi.IsOut, span: true)
                        ),
                        new TD { class_ = "paramname" }._(
                            new STRONG(pi.Name)
                        ),
                        new TD(parameterDocumentation == null
                            ? (object) new EM("This parameter is not documented.")
                            : interpretNodes(parameterDocumentation.Nodes(), req))
                    );
                })
            );
        }

        private bool sameSummary(XElement doc1, XElement doc2)
        {
            if (doc1 == null)
                return doc2 == null;
            else if (doc2 == null)
                return false;
            if (doc1.Element("summary") == null)
                return doc2.Element("summary") == null;
            else if (doc2.Element("summary") == null)
                return false;
            return XNode.DeepEquals(doc1.Element("summary"), doc2.Element("summary"));
        }

        private IEnumerable<object> formatMemberExtraInfo(HttpRequest req, MemberInfo member, bool markInterfaceMethods)
        {
            var listItems = formatMemberExtraInfoItems(req, member, markInterfaceMethods).ToList();
            if (listItems.Count > 0)
                yield return new UL { class_ = "extra" }._(listItems);
        }

        private IEnumerable<object> formatMemberExtraInfoItems(HttpRequest req, MemberInfo member, bool markInterfaceMethods)
        {
            var method = member as MethodInfo;
            if (method == null)
            {
                var prop = member as PropertyInfo;
                if (prop != null)
                    method = prop.GetGetMethod() ?? prop.GetSetMethod();
                else
                {
                    var evnt = member as EventInfo;
                    if (evnt != null)
                    {
                        method = evnt.GetAddMethod() ?? evnt.GetRemoveMethod();
                        if (method == null)
                            yield break;
                    }
                    else
                        yield break;
                }
            }

            MemberInfo baseDefinition = method.GetBaseDefinition();
            var basePropOrEvent = baseDefinition == null ? null :
                (MemberInfo) baseDefinition.DeclaringType.GetProperties().FirstOrDefault(p => p.GetGetMethod() == baseDefinition || p.GetSetMethod() == baseDefinition) ??
                (MemberInfo) baseDefinition.DeclaringType.GetEvents().FirstOrDefault(e => e.GetAddMethod() == baseDefinition || e.GetRemoveMethod() == baseDefinition);
            if (basePropOrEvent != null)
                baseDefinition = basePropOrEvent;
            if (method.DeclaringType.IsInterface)
            {
                if (markInterfaceMethods)
                    yield return new LI(member is MethodInfo ? "Interface method" : member is PropertyInfo ? "Interface property" : "Interface event");
            }
            else if (method.IsVirtual)
            {
                bool showVirtual = true;
                if (baseDefinition != member)
                {
                    string url = null, baseUrl = req.Url.WithPathOnly("").ToHref();
                    var dcmn = documentationCompatibleMemberName(baseDefinition);
                    if (_members.ContainsKey(dcmn))
                        url = req.Url.WithPathOnly("/" + dcmn.UrlEscape()).ToHref();
                    yield return new LI(new object[] { "Overrides: ", friendlyMemberName(baseDefinition, containingType: true, parameterTypes: true, url: url, baseUrl: baseUrl) });
                    if (method.IsFinal)
                        yield return new LI("Sealed");
                    showVirtual = false;
                }

                if (method.IsAbstract)
                {
                    showVirtual = false;
                    yield return new LI("Abstract");
                }

                foreach (var interf in method.ReflectedType.GetInterfaces())
                {
                    var map = method.ReflectedType.GetInterfaceMap(interf);
                    var index = map.TargetMethods.IndexOf(method);
                    if (index != -1)
                    {
                        string url = null, baseUrl = baseUrl = req.Url.WithPathOnly("").ToHref();
                        var interfaceMember =
                            (MemberInfo) interf.GetProperties().FirstOrDefault(p => p.GetGetMethod() == map.InterfaceMethods[index] || p.GetSetMethod() == map.InterfaceMethods[index]) ??
                            (MemberInfo) interf.GetEvents().FirstOrDefault(e => e.GetAddMethod() == map.InterfaceMethods[index] || e.GetRemoveMethod() == map.InterfaceMethods[index]) ??
                            map.InterfaceMethods[index];
                        var interfaceMemberDefinition = findInterfaceMemberDefinition(interfaceMember);
                        var dcmn = documentationCompatibleMemberName(interfaceMemberDefinition);
                        if (_members.ContainsKey(dcmn))
                            url = req.Url.WithPathOnly("/" + dcmn.UrlEscape()).ToHref();
                        yield return new LI("Implements: ", friendlyMemberName(interfaceMember, containingType: true, parameterTypes: true, url: url, baseUrl: baseUrl));
                        showVirtual = showVirtual && !method.IsFinal;
                    }
                }
                if (showVirtual)
                    yield return new LI("Virtual");
            }
        }

        private MemberInfo findInterfaceMemberDefinition(MemberInfo member)
        {
            var method = member as MethodInfo;
            if (method != null)
            {
                if (method.IsGenericMethod && !method.IsGenericMethodDefinition)
                    return findInterfaceMemberDefinition(method.GetGenericMethodDefinition());
                if (!method.DeclaringType.IsGenericType || method.DeclaringType.IsGenericTypeDefinition)
                    return method;
                var def = method.DeclaringType.GetGenericTypeDefinition();
                return def.GetMethods((method.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic) | (method.IsStatic ? BindingFlags.Static : BindingFlags.Instance))
                    .FirstOrDefault(m => sameExcept(m, method, def.GetGenericArguments(), method.DeclaringType.GetGenericArguments()));
            }
            var prop = member as PropertyInfo;
            if (prop != null)
                return member.DeclaringType.GetGenericTypeDefinition().GetProperties().FirstOrDefault(p => p.Name == prop.Name);
            var evnt = member as EventInfo;
            Ut.Assert(evnt != null);
            return member.DeclaringType.GetGenericTypeDefinition().GetEvents().FirstOrDefault(e => e.Name == evnt.Name);
        }

        private bool sameExcept(MethodInfo m1, MethodInfo m2, Type[] genericTypeParameters, Type[] genericTypeArguments)
        {
            if (m1.Name != m2.Name)
                return false;
            var p1 = m1.GetParameters();
            var p2 = m2.GetParameters();
            if (p1.Length != p2.Length)
                return false;
            for (int i = 0; i < p1.Length; i++)
                if (!sameExcept(p1[i].ParameterType, p2[i].ParameterType, genericTypeParameters, genericTypeArguments))
                    return false;
            return true;
        }

        private bool sameExcept(Type t1, Type t2, Type[] genericTypeParameters, Type[] genericTypeArguments)
        {
            if (t1 == t2)
                return true;
            if (t1.IsGenericParameter)
            {
                var index = genericTypeParameters.IndexOf(t1);
                if (index != -1 && genericTypeArguments[index] == t2)
                    return true;
            }

            if (t1.IsArray != t2.IsArray)
                return false;
            if (t1.IsArray)
                return sameExcept(t1.GetElementType(), t2.GetElementType(), genericTypeParameters, genericTypeArguments);

            if (t1.IsByRef != t2.IsByRef)
                return false;
            if (t1.IsByRef)
                return sameExcept(t1.GetElementType(), t2.GetElementType(), genericTypeParameters, genericTypeArguments);

            if (t1.IsPointer != t2.IsPointer)
                return false;
            if (t1.IsPointer)
                return sameExcept(t1.GetElementType(), t2.GetElementType(), genericTypeParameters, genericTypeArguments);

            if (!t1.IsGenericType || !t2.IsGenericType)
                return false;
            if (t1.GetGenericTypeDefinition() != t2.GetGenericTypeDefinition())
                return false;
            var parms = t1.GetGenericArguments();
            var args = t2.GetGenericArguments();
            Ut.Assert(parms.Length == args.Length);
            for (int i = 0; i < parms.Length; i++)
                if (!sameExcept(parms[i], args[i], genericTypeParameters, genericTypeArguments))
                    return false;
            return true;
        }

        private LI inheritsFrom(Type type, HttpRequest req)
        {
            if ((type.IsAbstract && type.IsSealed) || type.IsInterface)
                return null;

            return new LI(
                new A("Show inherited types...") { href = "#", onclick = "document.getElementById('inherited_link').style.display='none';document.getElementById('inherited_tree').style.display='block';return false;", id = "inherited_link" },
                new DIV { id = "inherited_tree", style = "display:none" }._(
                    "Inherits from:",
                    inheritsFromBullet(type.BaseType, req)
                ));
        }

        private object inheritsFromBullet(Type type, HttpRequest req)
        {
            if (type == null)
                return null;
            return new UL(new LI(friendlyTypeName(type, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true), type.IsSealed ? " (sealed)" : null, inheritsFromBullet(type.BaseType, req)));
        }

        private LI implementsInterfaces(Type type, HttpRequest req)
        {
            var infs = type.GetInterfaces();
            if (!infs.Any())
                return null;
            return new LI(
                new A("Show implemented interfaces...") { href = "#", onclick = "document.getElementById('implements_link').style.display='none';document.getElementById('implements_tree').style.display='block';return false;", id = "implements_link" },
                new DIV { id = "implements_tree", style = "display:none" }._(
                    "Implements:", new UL(infs
                        .Select(i => new { Interface = i, Directly = type.BaseType == null || !type.BaseType.GetInterfaces().Any(i2 => i2.Equals(i)) })
                        .OrderBy(inf => inf.Directly ? 0 : 1).ThenBy(inf => inf.Interface.Name)
                        .Select(inf => new LI(friendlyTypeName(inf.Interface, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true), inf.Directly ? " (directly)" : null))
                    )
                )
            );
        }

        private LI implementedBy(Type type, HttpRequest req)
        {
            var implementedBy = _types.Select(kvp => kvp.Value.Type).Where(t => t.GetInterfaces().Any(i => i.Equals(type) || (i.IsGenericType && i.GetGenericTypeDefinition().Equals(type)))).ToArray();
            if (!implementedBy.Any())
                return null;
            return new LI(
                new A("Show types that implement this interface...") { href = "#", onclick = "document.getElementById('implementedby_link').style.display='none';document.getElementById('implementedby_tree').style.display='block';return false;", id = "implementedby_link" },
                new DIV { id = "implementedby_tree", style = "display:none" }._(
                    "Implemented by:", new UL(implementedBy.Select(t => new LI(friendlyTypeName(t, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true))))
                )
            );
        }

        private LI derivedTypes(Type type, HttpRequest req)
        {
            var derivedTypes = _types.Select(kvp => kvp.Value.Type)
                .Where(t => t.BaseType == type || (t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == type))
                .ToArray();
            if (!derivedTypes.Any())
                return null;

            return new LI(
                new A("Show derived types...") { href = "#", onclick = "document.getElementById('derivedtypes_link').style.display='none';document.getElementById('derivedtypes_tree').style.display='block';return false;", id = "derivedtypes_link" },
                new DIV { id = "derivedtypes_tree", style = "display:none" }._(
                    "Derived types:", new UL(derivedTypes.Select(t => new LI(friendlyTypeName(t, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true))))
                )
            );
        }

        private IEnumerable<object> generateGenericTypeParameterTable(HttpRequest req, XElement document, Type[] genericTypeArguments)
        {
            if (!genericTypeArguments.Any())
                yield break;
            yield return new H2("Generic type parameters");
            yield return new TABLE { class_ = "doclist" }._(
                genericTypeArguments.Select(gta =>
                {
                    var constraints = gta.GetGenericParameterConstraints();
                    var docElem = document == null ? null : document.Elements("typeparam")
                        .Where(xe => xe.Attribute("name") != null && xe.Attribute("name").Value == gta.Name).FirstOrDefault();
                    return new TR(
                        new TD(new STRONG(gta.Name), formatGenericConstraints(req, constraints, gta.GenericParameterAttributes)),
                        new TD(docElem == null ? (object) new EM("This type parameter is not documented.") : interpretNodes(docElem.Nodes(), req))
                    );
                })
            );
        }

        private IEnumerable<object> formatGenericConstraints(HttpRequest req, Type[] constraints, GenericParameterAttributes genericParameterAttributes)
        {
            var infos = new List<object>();
            if (constraints != null && constraints.Length > 0)
                infos.Add(new object[] { "Must derive from: ", constraints.Select(c => friendlyTypeName(c, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true)).InsertBetween<object>(", "), "." });
            if (genericParameterAttributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
                infos.Add("Must have a default constructor.");
            if (genericParameterAttributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
                infos.Add("Must be a non-nullable value type.");
            if (genericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
                infos.Add("Must be a reference type.");
            if (genericParameterAttributes.HasFlag(GenericParameterAttributes.Covariant))
                infos.Add("Covariant.");
            if (genericParameterAttributes.HasFlag(GenericParameterAttributes.Contravariant))
                infos.Add("Contravariant.");
            if (infos.Count > 0)
                yield return new UL { class_ = "extra" }._(infos.Select(inf => new LI(inf)));
        }

        private object interpretNodes(IEnumerable<XNode> nodes, HttpRequest req)
        {
            return nodes.Select(n => interpretNode(n, req));
        }

        private IEnumerable<object> interpretNode(XNode node, HttpRequest req)
        {
            if (node is XText)
            {
                yield return ((XText) node).Value;
                yield break;
            }

            var elem = (XElement) node;

            if (elem.Name == "para")
                yield return new P(interpretNodes(elem.Nodes(), req));
            else if (elem.Name == "list" && elem.Attribute("type") != null && elem.Attribute("type").Value == "bullet")
                yield return new UL(elem.Elements("item").Select(e =>
                    e.Elements("term").Any()
                        ? (object) new LI(new STRONG(interpretNodes(e.Element("term").Nodes(), req)),
                            e.Elements("description").Any() ? new BLOCKQUOTE(interpretNodes(e.Element("description").Nodes(), req)) : null)
                        : e.Elements("description").Any()
                            ? new LI(interpretNodes(e.Element("description").Nodes(), req))
                            : null
                ));
            else if (elem.Name == "list" && elem.Attribute("type") != null && elem.Attribute("type").Value == "table")
                yield return new TABLE { class_ = "usertable" }._(elem.Elements("item").Select(e =>
                    new TR(
                        new TD(new STRONG(interpretNodes(e.Element("term").Nodes(), req))),
                        new TD(interpretNodes(e.Element("description").Nodes(), req))
                    )
                ));
            else if (elem.Name == "code")
                yield return new PRE(interpretPre(elem, req));
            else if (elem.Name == "see" && elem.Attribute("cref") != null)
                yield return interpretCref(elem.Attribute("cref").Value, req, false);
            else if (elem.Name == "c")
                yield return new CODE(interpretNodes(elem.Nodes(), req));
            else if (elem.Name == "paramref" && elem.Attribute("name") != null)
                yield return new SPAN { class_ = "parameter" }._(new EM(elem.Attribute("name").Value));
            else if (elem.Name == "typeparamref" && elem.Attribute("name") != null)
                yield return new SPAN { class_ = "parameter" }._(new EM(elem.Attribute("name").Value));
            else
            {
                yield return @"[Unrecognised tag: ""{0}""]".Fmt(elem.Name);
                yield return interpretNodes(elem.Nodes(), req);
            }
        }

        private object interpretCref(string token, HttpRequest req, bool includeNamespaces)
        {
            Type actual;
            if (_types.ContainsKey(token))
                return friendlyTypeName(_types[token].Type, includeNamespaces, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true);
            else if (_members.ContainsKey(token))
                return friendlyMemberName(_members[token].Member, parameterTypes: true, namespaces: includeNamespaces, url: req.Url.WithPathOnly("/" + token.UrlEscape()).ToHref(), baseUrl: req.Url.WithPathOnly("").ToHref());
            else if (token.StartsWith("T:") && (actual = Type.GetType(token.Substring(2), throwOnError: false, ignoreCase: true)) != null)
                return new SPAN { title = actual.FullName }._(foreignTypeName(actual, includeNamespaces));
            else if (token.StartsWith("T:"))
                return new SPAN { title = token.Substring(2) }._(foreignTypeName(token.Substring(2), includeNamespaces));
            else if (token.StartsWith("M:") || token.StartsWith("P:") || token.StartsWith("E:") || token.StartsWith("F:"))
                return CrefParser.Parse(token.Substring(2)).GetHtml(Assumption.Member,
                    member => friendlyMemberName(member, containingType: true, parameterTypes: true),
                    type => friendlyTypeName(type, includeOuterTypes: true, inclRef: true, span: true));
            else
                return new SPAN { title = token.Substring(2) }._("[Unrecognized cref attribute]");
        }

        private IEnumerable<object> foreignTypeName(Type type, bool includeNamespaces)
        {
            if (includeNamespaces)
            {
                yield return type.Namespace;
                yield return ".";
            }
            yield return new STRONG(Regex.Replace(type.Name, "`\\d+", string.Empty));
            if (type.IsGenericType)
            {
                yield return "<";
                yield return type.GetGenericArguments().First().Name;
                foreach (var gen in type.GetGenericArguments().Skip(1))
                {
                    yield return ", ";
                    yield return gen.Name;
                }
                yield return ">";
            }
        }

        private IEnumerable<object> foreignTypeName(string type, bool includeNamespaces)
        {
            var numGenerics = 0;

            var genericsPos = type.LastIndexOf('`');
            if (genericsPos != -1)
            {
                numGenerics = Convert.ToInt32(type.Substring(genericsPos + 1));
                type = type.Substring(0, genericsPos);
            }

            var namespacePos = type.LastIndexOf('.');
            string namesp = null;
            if (namespacePos != -1)
            {
                namesp = type.Substring(0, namespacePos);
                type = type.Substring(namespacePos + 1);
            }

            if (namesp != null && includeNamespaces)
            {
                yield return namesp;
                yield return ".";
            }

            yield return new STRONG(type);

            if (numGenerics > 0)
            {
                yield return "<";
                yield return Enumerable.Range(1, numGenerics).Select(i => "T" + i).JoinString(", ");
                yield return ">";
            }
        }

        private IEnumerable<object> interpretPre(XElement elem, HttpRequest req)
        {
            // Hideosly complex code to remove common indentation from each line, while allowing something like <see cref="..." /> inside a <code> element.
            // Example: suppose the input is "<code>\n    XYZ<see/>\n\n    ABC</code>". Note <see/> is an inline element.

            // Step 1: Turn all the text nodes into strings, split them at the newlines, then add the newlines back in; turn all the non-text nodes into HTML
            // Example is now: { "", E.N, "    XYZ", [<see> element], E.N, E.N, "    ABC" }       (E.N stands for Environment.NewLine)
            var everything = elem.Nodes().SelectMany(nod => nod is XText ? Regex.Split(((XText) nod).Value, @"\r\n|\r|\n").Select(lin => lin.TrimEnd()).InsertBetween<object>(Environment.NewLine) : interpretNode((XElement) nod, req));

            // Step 2: Split the collection at the newlines, giving a set of lines which contain strings and HTML elements
            // Example is now: { { "" }, { "    XYZ", [<see> element] }, { }, { "    ABC" } }
            // Put this into a list to avoid enumerating this entire computation twice.
            var lines = everything.Split(el => el is string && el.Equals(Environment.NewLine));
            lines = lines.ToList();

            // Step 3: Determine the common indentation of the lines beginning with strings.
            // (We are assuming here that you never get consecutive text nodes which might together produce a larger common indentation.
            // With current Visual Studio XML documentation generation and the .NET XML API, that doesn’t appear to occur.)
            // Example gives commonIndentation = 4
            var commonIndentation = lines.Min(lin =>
            {
                // Empty lines don’t count
                if (!lin.Any())
                    return null;

                // If the first item is not a string, the indentation is zero.
                var first = lin.First() as string;
                if (first == null)
                    return 0;

                // If the first item is an empty string, assume it’s the only item, so it’s a blank line
                if (first == "")
                    return null;

                return first.TakeWhile(c => c == ' ').Count();
            });

            // If the common indentation exists and is greater than 0, we know that every line must start with a string.
            // So remove the common indentation from all the strings that are the first element of each line.
            // Note that the use of SubstringSafe() elegantly handles the case of lines containing just an empty string.
            // Result in the example is now: { { "" }, { "XYZ", [<see> element] }, { }, { "ABC" } }
            if (commonIndentation != null && commonIndentation.Value > 0)
                lines = lines.Select(lin => lin.Select((item, index) => index == 0 ? ((string) item).SubstringSafe(commonIndentation.Value) : item));

            // Finally, put the newlines back in and return the result.
            // Result in the example is thus: { { "" }, E.N, { "XYZ", [<see> element] }, E.N, { }, E.N, { "ABC" } }
            return lines.InsertBetween<object>(Environment.NewLine);
        }

        private string stringSoup(object obj)
        {
            var sb = new StringBuilder();
            stringSoup(obj, sb);
            return sb.ToString();
        }

        private void stringSoup(object obj, StringBuilder sb)
        {
            if (obj == null)
                return;
            if (obj is string)
                sb.Append((string) obj);
            else if (obj is IEnumerable)
                foreach (var elem in (IEnumerable) obj)
                    stringSoup(elem, sb);
            else
                throw new InvalidOperationException(@"Encountered {0} where only strings are expected.".Fmt(obj.GetType().FullName));
        }

        public HttpResponse quickUrl(HttpRequest req)
        {
            var query = req.Url.Path.TrimStart('/');
            if (query.Length > 0)
            {
                var result =
                    quickUrlFinder(req, query, string.Equals) ??
                    quickUrlFinder(req, query, StringExtensions.EqualsNoCase) ??
                    quickUrlFinder(req, query, StringExtensions.ContainsNoCase);
                if (result != null)
                    return result;
            }
            return HttpResponse.Redirect(req.Url.WithPathParent().WithPath(""));
        }

        public HttpResponse quickUrlFinder(HttpRequest req, string query, Func<string, string, bool> matcher)
        {
            foreach (var inf in _members.Values)
                if (matcher(inf.Member.Name, query))
                    return HttpResponse.Redirect(req.Url.WithPathParent().WithPath("/" + documentationCompatibleMemberName(inf.Member).UrlEscape()).ToHref());
            foreach (var inf in _types.Values)
            {
                var pos = inf.Type.Name.IndexOf('`');
                var name = pos == -1 ? inf.Type.Name : inf.Type.Name.Substring(0, pos);
                if (matcher(name, query))
                    return HttpResponse.Redirect(req.Url.WithPathParent().WithPath("/" + getTypeFullName(inf.Type).UrlEscape()).ToHref());
            }
            foreach (var namesp in _namespaces.Keys)
            {
                var pos = namesp.LastIndexOf('.');
                var name = pos == -1 ? namesp : namesp.Substring(pos + 1);
                if (matcher(name, query))
                    return HttpResponse.Redirect(req.Url.WithPathParent().WithPath("/" + namesp.UrlEscape()).ToHref());
            }
            return null;
        }
    }
}
