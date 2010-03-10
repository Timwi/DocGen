using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Servers;
using RT.TagSoup.HtmlTags;
using RT.Util.Collections;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace RT.DocGen
{
    /// <summary>
    /// Provides an <see cref="HttpRequestHandler"/> that generates web pages from C# XML documentation.
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
            body, pre { font-family: ""Segoe UI"", ""Verdana"", sans-serif; font-size: 11pt; margin: .5em; }
            .namespace { font-weight: normal; color: #888; }
            a.namespace { color: #00e; }
            a.namespace:visited { color: #551a8b; }
            .sidebar { font-size: small; }
            .sidebar li { margin: 0; }
            .sidebar div.tree div.type { padding-left: 2em; }
            .sidebar div.tree div.member { padding-left: 2.5em; text-indent: -2.5em; }
            .sidebar div.tree div.type > div { font-weight: normal; }
            .sidebar div.tree div.type > div.line { font-weight: bold; padding-left: 0.5em; text-indent: -2.5em; }
            .type span.typeicon, .sidebar div.member span.icon { display: inline-block; width: 1.5em; margin-right: 0.5em; text-indent: 0; text-align: center; color: #000; -moz-border-radius: 0.7em 0.7em 0.7em 0.7em; }
            .sidebar div.legend div.type, .sidebar div.legend div.member { padding-left: 0; white-space: nowrap; }
            .Method { font-weight: normal; background: #eee; padding: 0.1em; }
            .sidebar .Method, pre .Method, table.doclist td.item .Method { background: transparent; padding: 0; }

            span.icon, span.typeicon { font-size: smaller; }
            td.type span.typeicon { font-size: normal; width: 2em; }
            td.type { font-weight: bold; white-space: nowrap; }

            .sidebar div.Constructor.member span.icon { background-color: #bfb; border: 2px solid #bfb; }
            .sidebar div.Method.member span.icon { background-color: #cdf; border: 2px solid #cdf; }
            .sidebar div.Property.member span.icon { background-color: #fcf; border: 2px solid #fcf; }
            .sidebar div.Event.member span.icon { background-color: #faa; border: 2px solid #faa; }
            .sidebar div.Field.member span.icon { background-color: #ee8; border: 2px solid #ee8; }

            .Class.type span.typeicon { background-color: #4df; border: 2px solid #4df; }
            .Struct.type span.typeicon { background-color: #f9f; border: 2px solid #f9f; }
            .Enum.type span.typeicon { background-color: #4f8; border: 2px solid #4f8; }
            .Interface.type span.typeicon { background-color: #f44; border: 2px solid #f44; }
            .Delegate.type span.typeicon { background-color: #ff4; border: 2px solid #ff4; }
            .type.missing span.typeicon { border-color: red; }

            .sidebar div.legend, .sidebar div.tree, .sidebar div.auth { background: #f8f8f8; border: 1px solid black; -moz-border-radius: 5px; padding: .5em; margin-bottom: .7em; }
            .sidebar div.auth { text-align: center; }
            .sidebar div.legend p { text-align: center; font-weight: bold; margin: 0 0 0.4em 0; padding: 0.2em 0; background: #ddd; }
            .sidebar ul.tree { margin: .5em 0; padding: 0 0 0 2em; }
            .sidebar div.member.highlighted { background: #bdf; }
            .sidebar div.member.highlighted span.icon { border-color: black; }
            .sidebar div.member.missing span.icon { border-color: red; }

            ul { padding-left: 1.5em; margin-bottom: 1em; }
            li { margin-top: 0.7em; margin-bottom: 0.7em; }
            li li { margin: 0; }
            table { border-collapse: collapse; }
            table.layout { border: hidden; width: 100%; margin: 0; }
            table.layout td { vertical-align: top; padding: 0; }
            table.layout td.content { width: 100%; padding: 1em 1em 0em 1.5em; }
            table.doclist td { border: 1px solid #ccc; padding: 1em 2em; background: #eee; }
            table.usertable td { padding: 0.2em 0.8em; }
            td p:first-child { margin-top: 0; }
            td p:last-child { margin-bottom: 0; }
            span.parameter, span.member { white-space: nowrap; }
            h1 span.parameter, h1 span.member { white-space: normal; }
            pre { background: #eee; border: 1px solid #ccc; padding: 1em 2em; }
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
        /// Initialises a <see cref="DocumentationGenerator"/> instance by searching the given path for XML and DLL files.
        /// All pairs of matching <c>*.dll</c> and <c>*.docs.xml</c> files are considered for documentation. The classes are extracted
        /// from the DLLs and grouped by namespaces.
        /// </summary>
        /// <param name="paths">Paths containing DLL and XML files.</param>
        public DocumentationGenerator(string[] paths) : this(paths, null, null) { }

        /// <summary>
        /// Initialises a <see cref="DocumentationGenerator"/> instance by searching the given path for XML and DLL files.
        /// All pairs of matching <c>*.dll</c> and <c>*.docs.xml</c> files are considered for documentation. The classes are extracted
        /// from the DLLs and grouped by namespaces.
        /// </summary>
        /// <param name="paths">Paths containing DLL and XML files.</param>
        /// <param name="usernamePasswordFile">Path to a file containing usernames and password hashes. If null, access is completely unrestricted.</param>
        public DocumentationGenerator(string[] paths, string usernamePasswordFile) : this(paths, usernamePasswordFile, null) { }

        /// <summary>
        /// Initialises a <see cref="DocumentationGenerator"/> instance by searching the given path for XML and DLL files.
        /// All pairs of matching <c>*.dll</c> and <c>*.docs.xml</c> files are considered for documentation. The classes are extracted
        /// from the DLLs and grouped by namespaces.
        /// </summary>
        /// <param name="paths">Paths containing DLL and XML files.</param>
        /// <param name="usernamePasswordFile">Path to a file containing usernames and password hashes. If null, access is completely unrestricted.</param>
        /// <param name="copyDllFilesTo">Path to copy DLL files to prior to loading them into memory. If null, original DLLs are loaded.</param>
        public DocumentationGenerator(string[] paths, string usernamePasswordFile, string copyDllFilesTo)
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

                foreach (var f in dllFiles.Where(f => File.Exists(f.FullName.Remove(f.FullName.Length - 3) + "docs.xml")))
                {
                    string loadFromFile = copyDllFilesTo != null ? Path.Combine(copyDllFilesTo, f.Name) : f.FullName;
                    try
                    {
                        var docsFile = f.FullName.Remove(f.FullName.Length - 3) + "docs.xml";
                        Assembly a = Assembly.LoadFile(loadFromFile);
                        XElement e = XElement.Load(docsFile);

                        foreach (var t in a.GetExportedTypes().Where(t => shouldTypeBeDisplayed(t)))
                        {
                            var typeFullName = GetTypeFullName(t);
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
                        _assemblyLoadErrors.Add(new Tuple<string, string>(loadFromFile, exc.Message + " (" + exc.GetType().FullName + ")"));
                        continue;
                    }
                    _assembliesLoaded.Add(loadFromFile);
                }
            }
        }

        private string GetTypeFullName(Type t)
        {
            return "T:" + (t.IsGenericType ? t.GetGenericTypeDefinition() : t).FullName.TrimEnd('&').Replace("+", ".");
        }

        /// <summary>
        /// Returns the <see cref="HttpRequestHandler"/> that handles HTTP requests for the documentation.
        /// Instantiate a <see cref="HttpRequestHandlerHook"/> with this and add it to an instance of
        /// <see cref="HttpServer"/> using <see cref="HttpServer.RequestHandlerHooks"/>.
        /// </summary>
        /// <returns>An <see cref="HttpRequestHandler"/> that can be hooked to an instance of <see cref="HttpServer"/></returns>
        public HttpResponse Handler(HttpRequest req)
        {
            if (req.RestUrlWithoutQuery == "")
                return HttpServer.RedirectResponse(req.BaseUrl + "/");

            if (req.RestUrlWithoutQuery == "/css")
                return HttpServer.StringResponse(_css, "text/css; charset=utf-8");

            return Session.Enable<docGenSession>(req, session =>
            {
                if (req.RestUrlWithoutQuery == "/login")
                    return Authentication.LoginHandler(req, _usernamePasswordFile, u => session.Username = u, req.BaseUrl, "the documentation");

                if (session.Username == null && _usernamePasswordFile != null)
                    return HttpServer.RedirectResponse(req.BaseUrl + "/login?returnto=" + req.Url.UrlEscape());

                if (req.RestUrlWithoutQuery == "/logout")
                {
                    session.CloseAction = SessionCloseAction.Delete;
                    return HttpServer.RedirectResponse(req.BaseUrl);
                }

                if (req.RestUrlWithoutQuery == "/changepassword")
                    return Authentication.ChangePasswordHandler(req, _usernamePasswordFile, req.BaseUrl, true);

                string ns = null;
                Type type = null;
                MemberInfo member = null;

                string token = req.RestUrl.Substring(1).UrlUnescape();
                if (_namespaces.ContainsKey(token))
                    ns = token;
                else if (_types.ContainsKey(token))
                {
                    type = _types[token].Type;
                    ns = type.Namespace;
                }
                else if (_members.ContainsKey(token))
                {
                    member = _members[token].Member;
                    type = member.DeclaringType;
                    ns = type.Namespace;
                }

                HttpStatusCode status = ns == null && req.RestUrl.Length > 1 ? HttpStatusCode._404_NotFound : HttpStatusCode._200_OK;

                var isstatic = isStatic(member);
                var html = new HTML(
                    new HEAD(
                        new TITLE(
                            member != null ? (
                                member.MemberType == MemberTypes.Constructor ? "Constructor: " :
                                member.MemberType == MemberTypes.Event ? "Event: " :
                                member.MemberType == MemberTypes.Field && isstatic ? "Static field: " :
                                member.MemberType == MemberTypes.Field ? "Field: " :
                                member.MemberType == MemberTypes.Method && isstatic ? "Static method: " :
                                member.MemberType == MemberTypes.Method ? "Method: " :
                                member.MemberType == MemberTypes.Property && isstatic ? "Static property: " :
                                member.MemberType == MemberTypes.Property ? "Property: " : "Member: "
                            ) : type != null ? (
                                type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : typeof(Delegate).IsAssignableFrom(type) ? "Delegate: " : "Class: "
                            ) : ns != null ? "Namespace: " : null,
                            member != null && member.MemberType == MemberTypes.Constructor ? (object) friendlyTypeNameInner(type, false, true, null, false, false) :
                                member != null ? member.Name : type != null ? (object) friendlyTypeNameInner(type, false, true, null, false, false) : ns != null ? ns : null,
                            member != null || type != null || ns != null ? " – " : null,
                            "XML documentation"
                        ),
                        new LINK { href = req.BaseUrl + "/css", rel = "stylesheet", type = "text/css" }
                    ),
                    new BODY(
                        new TABLE { class_ = "layout" }._(
                            new TR(
                                new TD { class_ = "sidebar" }._(
                                    new DIV { class_ = "tree" }._(
                                        new UL(_namespaces.Select(nkvp => new LI { class_ = "namespace" }._(new A(nkvp.Key) { href = req.BaseUrl + "/" + nkvp.Key.UrlEscape() },
                                            ns == null || ns != nkvp.Key ? null :
                                            nkvp.Value.Types.Where(tkvp => !tkvp.Value.Type.IsNested).Select(tkvp => generateTypeBullet(tkvp.Key, type, member, req))
                                        )))
                                    ),
                                    new DIV { class_ = "legend" }._(
                                        new P("Legend"),
                                        new TABLE { width = "100%" }._(
                                            new TR(
                                                new TD(
                                                    new DIV(new SPAN("Cl") { class_ = "typeicon" }, "Class") { class_ = "Class type" },
                                                    new DIV(new SPAN("St") { class_ = "typeicon" }, "Struct") { class_ = "Struct type" },
                                                    new DIV(new SPAN("En") { class_ = "typeicon" }, "Enum") { class_ = "Enum type" },
                                                    new DIV(new SPAN("In") { class_ = "typeicon" }, "Interface") { class_ = "Interface type" },
                                                    new DIV(new SPAN("De") { class_ = "typeicon" }, "Delegate") { class_ = "Delegate type" }
                                                ),
                                                new TD(
                                                    new DIV(new SPAN("C") { class_ = "icon" }, "Constructor") { class_ = "Constructor member" },
                                                    new DIV(new SPAN("M") { class_ = "icon" }, "Method") { class_ = "Method member" },
                                                    new DIV(new SPAN("P") { class_ = "icon" }, "Property") { class_ = "Property member" },
                                                    new DIV(new SPAN("E") { class_ = "icon" }, "Event") { class_ = "Event member" },
                                                    new DIV(new SPAN("F") { class_ = "icon" }, "Field") { class_ = "Field member" }
                                                )
                                            )
                                        )
                                    ),
                                    _usernamePasswordFile == null ? null : new DIV { class_ = "auth" }._(
                                        new A("Logout") { href = req.BaseUrl + "/logout" }, " | ", new A("Change password") { href = req.BaseUrl + "/changepassword?returnto=" + req.Url.UrlEscape() }
                                    )
                                ),
                                new TD { class_ = "content" }._(
                                    member != null && _members.ContainsKey(token) ?
                                        (object) generateMemberDocumentation(_members[token].Member, _members[token].Documentation, req) :
                                    type != null && _types.ContainsKey(token) ?
                                        (object) generateTypeDocumentation(_types[token].Type, _types[token].Documentation, req) :
                                    ns != null && _namespaces.ContainsKey(ns) ?
                                        (object) generateNamespaceDocumentation(ns, req) :
                                    req.RestUrl == "/"
                                        ? new DIV("Select an item from the list on the left.") { class_ = "warning" }
                                        : new DIV("This item is not documented.") { class_ = "warning" }
                                )
                            )
                        )
                    )
                );

                return new HttpResponse
                {
                    Status = status,
                    Headers = new HttpResponseHeaders { ContentType = "text/html; charset=utf-8" },
                    Content = new DynamicContentStream(html.ToEnumerable(), true)
                };
            });
        }

        private object friendlyTypeName(Type t, bool includeNamespaces, bool includeOuterTypes, bool span)
        {
            return friendlyTypeName(t, includeNamespaces, includeOuterTypes, null, false, span);
        }
        private object friendlyTypeName(Type t, bool includeNamespaces, bool includeOuterTypes, string baseURL, bool inclRef, bool span)
        {
            if (span)
                return new SPAN(friendlyTypeNameInner(t, includeNamespaces, includeOuterTypes, baseURL, inclRef, span)) { class_ = "type", title = t.FullName };
            else
                return friendlyTypeNameInner(t, includeNamespaces, includeOuterTypes, baseURL, inclRef, span);
        }
        private IEnumerable<object> friendlyTypeNameInner(Type t, bool includeNamespaces, bool includeOuterTypes, string baseURL, bool inclRef, bool span)
        {
            if (t.IsByRef)
            {
                if (inclRef)
                    yield return "ref ";
                t = t.GetElementType();
            }

            if (t.IsArray)
            {
                yield return friendlyTypeName(t.GetElementType(), includeNamespaces, includeOuterTypes, span);
                yield return "[]";
                yield break;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                yield return friendlyTypeName(t.GetGenericArguments()[0], includeNamespaces, includeOuterTypes, span);
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
                if (includeOuterTypes && t.IsNested && !t.IsGenericParameter)
                {
                    yield return friendlyTypeName(t.DeclaringType, includeNamespaces, true, baseURL, false, span);
                    yield return ".";
                }
                else if (includeNamespaces && !t.IsGenericParameter)
                {
                    yield return new SPAN(t.Namespace, ".") { class_ = "namespace" };
                }

                // Determine whether this type has its own generic type parameters.
                // This is different from being a generic type: a nested type of a generic type is automatically a generic type too, even though it doesn't have generic parameters of its own.
                var hasGenericTypeParameters = t.Name.Contains('`');

                string ret = hasGenericTypeParameters ? t.Name.Remove(t.Name.IndexOf('`')) : t.Name.TrimEnd('&');
                if (baseURL != null && !t.IsGenericParameter && _types.ContainsKey(GetTypeFullName(t)))
                    yield return new A(ret) { href = baseURL + "/" + GetTypeFullName(t).UrlEscape() };
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
                        yield return friendlyTypeName(ga, includeNamespaces, includeOuterTypes, baseURL, inclRef, span);
                    }
                    yield return ">";
                }
            }
        }

        private object friendlyMemberName(MemberInfo m, bool returnType, bool containingType, bool parameterTypes, bool parameterNames, bool namespaces)
        {
            return friendlyMemberName(m, returnType, containingType, parameterTypes, parameterNames, namespaces, false, null, null);
        }
        private object friendlyMemberName(MemberInfo m, bool returnType, bool containingType, bool parameterTypes, bool parameterNames, bool namespaces, bool indent)
        {
            return friendlyMemberName(m, returnType, containingType, parameterTypes, parameterNames, namespaces, indent, null, null);
        }
        private object friendlyMemberName(MemberInfo m, bool returnType, bool containingType, bool parameterTypes, bool parameterNames, bool namespaces, bool indent, string url, string baseUrl)
        {
            if (m.MemberType == MemberTypes.NestedType)
                return friendlyTypeName((Type) m, namespaces, true, baseUrl, false, true);

            if (m.MemberType == MemberTypes.Constructor || m.MemberType == MemberTypes.Method)
                return new SPAN { class_ = m.MemberType.ToString() }._(
                    friendlyMethodName(m, returnType, containingType, parameterTypes, parameterNames, namespaces, indent, url, baseUrl, false)
                );

            return new SPAN { class_ = m.MemberType.ToString() }._(
                returnType && m.MemberType == MemberTypes.Property ? new object[] { friendlyTypeName(((PropertyInfo) m).PropertyType, namespaces, true, baseUrl, false, true), " " } :
                returnType && m.MemberType == MemberTypes.Event ? new object[] { friendlyTypeName(((EventInfo) m).EventHandlerType, namespaces, true, baseUrl, false, true), " " } :
                returnType && m.MemberType == MemberTypes.Field ? new object[] { friendlyTypeName(((FieldInfo) m).FieldType, namespaces, true, baseUrl, false, true), " " } : null,
                containingType ? friendlyTypeName(m.DeclaringType, namespaces, true, baseUrl, false, true) : null,
                containingType ? "." : null,
                m.MemberType == MemberTypes.Property
                    ? (object) friendlyPropertyName((PropertyInfo) m, parameterTypes, parameterNames, namespaces, indent, url, baseUrl)
                    : new STRONG(url == null ? (object) m.Name : new A(m.Name) { href = url })
            );
        }

        private IEnumerable<object> friendlyMethodName(MemberInfo m, bool returnType, bool containingType, bool parameterTypes, bool parameterNames, bool namespaces, bool indent, string url, string baseUrl, bool isDelegate)
        {
            MethodBase mi = m as MethodBase;
            if (isDelegate)
                yield return "delegate ";
            if (returnType && m.MemberType != MemberTypes.Constructor)
            {
                var meth = (MethodInfo) m;
                // For the cast operators, omit the return type because they later become part of the operator name
                if (meth.Name != "op_Implicit" && meth.Name != "op_Explicit")
                {
                    yield return friendlyTypeName(meth.ReturnType, namespaces, true, baseUrl, false, true);
                    yield return " ";
                }
            }
            if ((m.MemberType == MemberTypes.Constructor || isDelegate) && url != null)
                yield return new STRONG(new A(friendlyTypeName(mi.DeclaringType, namespaces, isDelegate, true)) { href = url });
            else if (isDelegate)
                yield return new STRONG(friendlyTypeName(mi.DeclaringType, namespaces, true, baseUrl, false, true));
            else if (containingType)
                yield return friendlyTypeName(mi.DeclaringType, namespaces, true, baseUrl, false, true);
            else if (m.MemberType == MemberTypes.Constructor)
                yield return new STRONG(friendlyTypeName(mi.DeclaringType, namespaces, false, baseUrl, false, true));
            if (!indent && (mi.IsGenericMethod || (parameterNames && mi.GetParameters().Any()))) yield return new WBR();
            if (m.MemberType != MemberTypes.Constructor && !isDelegate)
            {
                if (containingType) yield return ".";
                yield return new STRONG(url == null
                    ? (object) cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, false, true, true))
                    : new A(cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, false, true, true))) { href = url }
                );
            }
            if (mi.IsGenericMethod)
            {
                if (!indent) yield return new WBR();
                yield return "<" + mi.GetGenericArguments().Select(ga => ga.Name).JoinString(", ") + ">";
            }
            if (parameterTypes || parameterNames)
            {
                yield return indent && mi.GetParameters().Any() ? "(\n    " : "(";
                if (!indent && mi.GetParameters().Any()) yield return new WBR();
                bool first = true;
                foreach (var p in mi.GetParameters())
                {
                    if (!first) yield return indent ? ",\n    " : ", ";
                    first = false;
                    yield return new SPAN { class_ = "parameter" }._(
                       parameterTypes && p.IsOut ? "out " : null,
                       parameterTypes ? friendlyTypeName(p.ParameterType, namespaces, true, baseUrl, !p.IsOut, true) : null,
                       parameterTypes && parameterNames ? " " : null,
                       parameterNames ? new STRONG(p.Name) : null
                   );
                }
                yield return indent && mi.GetParameters().Any() ? "\n)" : ")";
            }
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

        private IEnumerable<object> friendlyPropertyName(PropertyInfo property, bool parameterTypes, bool parameterNames, bool namespaces, bool indent, string url, string baseUrl)
        {
            var prms = property.GetIndexParameters();
            if (prms.Length > 0)
            {
                yield return new STRONG(url == null ? (object) "this" : new A("this") { href = url });
                yield return indent ? "[\n    " : "[";
                if (!indent) yield return new WBR();
                bool first = true;
                foreach (var p in prms)
                {
                    if (!first) yield return indent ? ",\n    " : ", ";
                    first = false;
                    yield return new SPAN { class_ = "parameter" }._(
                        parameterTypes && p.IsOut ? "out " : null,
                        parameterTypes ? friendlyTypeName(p.ParameterType, namespaces, true, baseUrl, !p.IsOut, true) : null,
                        parameterTypes && parameterNames ? " " : null,
                        parameterNames ? new STRONG(p.Name) : null
                    );
                }
                yield return indent ? "\n]" : "]";
            }
            else
                yield return new STRONG(url == null ? (object) property.Name : new A(property.Name) { href = url });
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
                return true;
            if (m.MemberType == MemberTypes.Field)
                return !(m as FieldInfo).IsPrivate && !isEventField(m);
            if (m.MemberType == MemberTypes.NestedType)
                return shouldTypeBeDisplayed((Type) m);

            return true;
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
                sb.Append(mi.DeclaringType.FullName.Replace("+", "."));
                sb.Append(m.MemberType == MemberTypes.Method ? "." + mi.Name : ".#ctor");
                if (mi.IsGenericMethod)
                {
                    sb.Append("``");
                    sb.Append(mi.GetGenericArguments().Count());
                }
                bool first = true;
                foreach (var p in mi.GetParameters())
                {
                    sb.Append(first ? "(" : ",");
                    first = false;
                    sb.Append(stringifyParameterType(p.ParameterType, mi, m.ReflectedType));
                }
                if (!first) sb.Append(")");
            }
            else if (m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Event)
            {
                sb.Append(m.MemberType == MemberTypes.Field ? "F:" : m.MemberType == MemberTypes.Property ? "P:" : "E:");
                sb.Append(m.DeclaringType.FullName.Replace("+", "."));
                sb.Append(".");
                sb.Append(m.Name);
            }
            else if (m.MemberType == MemberTypes.NestedType)
            {
                sb.Append(GetTypeFullName((Type) m));
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
                sb.Append("~" + stringifyParameterType(b.ReturnType, b, b.ReflectedType));
            }
            return sb.ToString();
        }

        private string stringifyParameterType(Type parameterType, MethodBase method, Type type)
        {
            if (parameterType.IsByRef)
                return stringifyParameterType(parameterType.GetElementType(), method, type) + "@";

            if (parameterType.IsArray)
                return stringifyParameterType(parameterType.GetElementType(), method, type) + "[]";

            if (!parameterType.IsGenericType && !parameterType.IsGenericParameter)
                return parameterType.FullName.Replace("+", ".");

            if (parameterType.IsGenericParameter)
            {
                int i = 0;
                if (method.IsGenericMethodDefinition)
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
                throw new Exception("Parameter type is a generic type, but its generic argument is neither in the class nor method/constructor definition.");
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
                    constructName += fullName.Substring(0, m.Index) + "{" + genericArguments.Take(num).Select(g => stringifyParameterType(g, method, type)).JoinString(",") + "}";
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

        private object generateTypeBullet(string typeFullName, Type selectedType, MemberInfo selectedMember, HttpRequest req)
        {
            var typeinfo = _types[typeFullName];
            string cssClass = typeinfo.GetTypeCssClass() + " type";
            if (typeinfo.Documentation == null) cssClass += " missing";
            if (typeinfo.Type == selectedType) cssClass += " highlighted";
            return new DIV { class_ = cssClass }._(new DIV(new SPAN(typeinfo.GetTypeLetters()) { class_ = "typeicon" }, new A(friendlyTypeName(typeinfo.Type, false, false, true)) { href = req.BaseUrl + "/" + typeFullName.UrlEscape() }) { class_ = "line" },
                selectedType == null || !isNestedTypeOf(selectedType, typeinfo.Type) || typeof(Delegate).IsAssignableFrom(typeinfo.Type) ? null :
                typeinfo.Members.Where(mkvp => isPublic(mkvp.Value.Member)).Select(mkvp =>
                {
                    string css = mkvp.Value.Member.MemberType.ToString() + " member";
                    if (mkvp.Value.Documentation == null) css += " missing";
                    if (mkvp.Value.Member == selectedMember) css += " highlighted";
                    return mkvp.Value.Member.MemberType != MemberTypes.NestedType
                        ? new DIV { class_ = css }._(new SPAN(mkvp.Value.Member.MemberType.ToString()[0]) { class_ = "icon" }, new A(friendlyMemberName(mkvp.Value.Member, false, false, true, false, false)) { href = req.BaseUrl + "/" + mkvp.Key.UrlEscape() })
                        : generateTypeBullet(GetTypeFullName((Type) mkvp.Value.Member), selectedType, selectedMember, req);
                })
            );
        }

        private bool isNestedTypeOf(Type nestedType, Type containingType)
        {
            if (nestedType == containingType) return true;
            if (!nestedType.IsNested) return false;
            return isNestedTypeOf(nestedType.DeclaringType, containingType);
        }

        private IEnumerable<object> generateNamespaceDocumentation(string namespaceName, HttpRequest req)
        {
            yield return new H1("Namespace: ", namespaceName);
            yield return new TABLE { class_ = "doclist" }._(
                _namespaces[namespaceName].Types.Where(t => !t.Value.Type.IsNested).Select(kvp =>
                {
                    string cssClass = kvp.Value.GetTypeCssClass() + " type";
                    if (kvp.Value.Documentation == null) cssClass += " missing";
                    return new TR(
                        new TD { class_ = cssClass }._(new SPAN(kvp.Value.GetTypeLetters()) { class_ = "typeicon" }, new A(friendlyTypeName(kvp.Value.Type, false, false, true)) { href = req.BaseUrl + "/" + kvp.Key.UrlEscape() }),
                        new TD(kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                            ? (object) new EM("This type is not documented.")
                            : interpretNodes(kvp.Value.Documentation.Element("summary").Nodes(), req)));
                })
            );
        }

        private IEnumerable<object> generateMemberDocumentation(MemberInfo member, XElement document, HttpRequest req)
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
                friendlyMemberName(member, true, false, true, false, false)
            );
            yield return new UL(new LI("Declared in: ", friendlyTypeName(member.DeclaringType, true, true, req.BaseUrl, false, true)));
            yield return new H2("Declaration");
            yield return new PRE((isStatic ? "static " : null), friendlyMemberName(member, true, false, true, true, false, true, null, req.BaseUrl));

            if (document != null)
            {
                var summary = document.Element("summary");
                if (summary != null)
                {
                    yield return new H2("Summary");
                    yield return interpretNodes(summary.Nodes(), req);
                }
                var returns = document.Element("returns");
                if (returns != null)
                {
                    yield return new H2("Returns");
                    yield return interpretNodes(returns.Nodes(), req);
                }
                var exceptions = document.Elements("exception").Where(elem => elem.Attribute("cref") != null);
                if (exceptions.Any())
                {
                    yield return new H2("Exceptions");
                    yield return new UL(exceptions.Select(exc => new LI(interpretCref(exc.Attribute("cref").Value, req, true), new BLOCKQUOTE(interpretNodes(exc.Nodes(), req)))));
                }
                var remarks = document.Element("remarks");
                if (remarks != null)
                {
                    yield return new H2("Remarks");
                    yield return interpretNodes(remarks.Nodes(), req);
                }
                foreach (var example in document.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretNodes(example.Nodes(), req);
                }
                var seealsos = document.Elements("see").Concat(document.Elements("seealso"));
                if (seealsos.Any())
                {
                    yield return new H2("See also");
                    yield return new UL(seealsos.Select(sa => new LI(interpretCref(sa.Attribute("cref").Value, req, true))));
                }
            }

            if ((member.MemberType == MemberTypes.Constructor || member.MemberType == MemberTypes.Method) && (member as MethodBase).GetParameters().Any())
            {
                yield return new H2("Parameters");
                yield return new TABLE { class_ = "doclist" }._(
                    (member as MethodBase).GetParameters().Select(pi =>
                    {
                        var docElem = document == null ? null : document.Elements("param")
                            .Where(xe => xe.Attribute("name") != null && xe.Attribute("name").Value == pi.Name).FirstOrDefault();
                        return new TR(
                            new TD { class_ = "item" }._(
                                pi.IsOut ? "out " : null,
                                friendlyTypeName(pi.ParameterType, false, true, req.BaseUrl, !pi.IsOut, true),
                                " ",
                                new STRONG(pi.Name)
                            ),
                            new TD(docElem == null
                                ? (object) new EM("This parameter is not documented.")
                                : interpretNodes(docElem.Nodes(), req))
                        );
                    })
                );
            }

            if (member.MemberType == MemberTypes.Method && ((MethodBase) member).IsGenericMethod)
                yield return generateGenericTypeParameterTable(((MethodBase) member).GetGenericArguments(), document, req);
        }

        private IEnumerable<object> generateTypeDocumentation(Type type, XElement document, HttpRequest req)
        {
            bool isDelegate = typeof(Delegate).IsAssignableFrom(type);

            yield return new H1(
                type.IsNested
                    ? (isDelegate ? "Nested delegate: " : type.IsEnum ? "Nested enum: " : type.IsValueType ? "Nested struct: " : type.IsInterface ? "Nested interface: " : (type.IsAbstract && type.IsSealed) ? "Nested static class: " : type.IsAbstract ? "Nested abstract class: " : "Nested class: ")
                    : (isDelegate ? "Delegate: " : type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : (type.IsAbstract && type.IsSealed) ? "Static class: " : type.IsAbstract ? "Abstract class: " : "Class: "),
                friendlyTypeName(type, true, true, true)
            );

            LI typeTree = null;
            if ((!type.IsAbstract || !type.IsSealed) && !type.IsInterface)
            {
                var typeRecurse = type;
                while (typeRecurse != null)
                {
                    var ftn = friendlyTypeName(typeRecurse, true, true, typeRecurse == type ? null : req.BaseUrl, false, true);
                    typeTree = new LI(typeRecurse == typeof(object) ? "Inheritance:" : typeRecurse == type ? (object) new STRONG(ftn) : ftn, typeRecurse.IsSealed ? " (sealed)" : null, typeTree == null ? null : new UL(typeTree));
                    typeRecurse = typeRecurse.BaseType;
                }
            }
            LI interfaces = null;
            var infs = type.GetInterfaces();
            if (infs.Any())
                interfaces = new LI("Implements:", new UL(infs.Select(i => new LI(friendlyTypeName(i, true, true, true)))));

            object derived = null;
            if (type.IsInterface)
            {
                var implementedBy = _types.Select(kvp => kvp.Value.Type).Where(t => t.GetInterfaces().Contains(type)).ToArray();
                if (implementedBy.Any())
                    derived = new LI("Implemented by:", new UL(implementedBy.Select(t => new LI(friendlyTypeName(t, true, true, req.BaseUrl, false, true)))));
            }
            else
            {
                var derivedTypes = _types
                    .Select(kvp => kvp.Value.Type)
                    .Where(t => t.BaseType == type || (t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == type))
                    .ToArray();
                if (derivedTypes.Any())
                    derived = new LI("Derived types:", new UL(derivedTypes.Select(t => new LI(friendlyTypeName(t, true, true, req.BaseUrl, false, true)))));
            }

            yield return new UL { class_ = "typeinfo" }._(
                new LI("Namespace: ", new A(type.Namespace) { class_ = "namespace", href = req.BaseUrl + "/" + type.Namespace.UrlEscape() }),
                type.IsNested ? new LI("Declared in: ", friendlyTypeName(type.DeclaringType, true, true, req.BaseUrl, false, true)) : null,
                typeTree, interfaces, derived
            );

            MethodInfo m = null;
            if (isDelegate)
            {
                m = type.GetMethod("Invoke");
                yield return new H2("Declaration");
                yield return new PRE(friendlyMethodName(m, true, false, true, true, true, true, null, req.BaseUrl, true));
            }

            if (document == null)
                yield return new DIV(new EM("This type is not documented.")) { class_ = "warning" };
            else
            {
                var summary = document.Element("summary");
                if (summary != null)
                {
                    yield return new H2("Summary");
                    yield return interpretNodes(summary.Nodes(), req);
                }

                if (isDelegate && m.GetParameters().Any())
                {
                    yield return new H2("Parameters");
                    yield return new TABLE { class_ = "doclist" }._(
                        m.GetParameters().Select(pi =>
                        {
                            var docElem = document == null ? null : document.Elements("param")
                                .Where(xe => xe.Attribute("name") != null && xe.Attribute("name").Value == pi.Name).FirstOrDefault();
                            return new TR(
                                new TD { class_ = "item" }._(
                                    pi.IsOut ? "out " : null,
                                    friendlyTypeName(pi.ParameterType, false, true, req.BaseUrl, !pi.IsOut, true),
                                    " ",
                                    new STRONG(pi.Name)
                                ),
                                new TD(docElem == null
                                    ? (object) new EM("This parameter is not documented.")
                                    : interpretNodes(docElem.Nodes(), req))
                            );
                        })
                    );
                }

                if (type.IsGenericType)
                {
                    var args = type.GetGenericArguments();
                    if (type.DeclaringType != null && type.DeclaringType.IsGenericType)
                        args = args.Skip(type.DeclaringType.GetGenericArguments().Length).ToArray();
                    yield return generateGenericTypeParameterTable(args, document, req);
                }

                var remarks = document.Element("remarks");
                if (remarks != null)
                {
                    yield return new H2("Remarks");
                    yield return interpretNodes(remarks.Nodes(), req);
                }
                foreach (var example in document.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretNodes(example.Nodes(), req);
                }
            }

            if (!isDelegate)
            {
                foreach (var gr in _types[GetTypeFullName(type)].Members.Where(kvp => isPublic(kvp.Value.Member)).GroupBy(kvp => new
                {
                    MemberType = kvp.Value.Member.MemberType,
                    IsStatic = isStatic(kvp.Value.Member)
                }))
                {
                    yield return new H2(
                        gr.Key.MemberType == MemberTypes.Constructor ? "Constructors" :
                        gr.Key.MemberType == MemberTypes.Event ? "Events" :
                        gr.Key.MemberType == MemberTypes.Field ? "Fields" :
                        gr.Key.MemberType == MemberTypes.Method && gr.Key.IsStatic ? "Static methods" :
                        gr.Key.MemberType == MemberTypes.Method ? "Instance methods" :
                        gr.Key.MemberType == MemberTypes.Property && gr.Key.IsStatic ? "Static properties" :
                        gr.Key.MemberType == MemberTypes.Property ? "Instance properties" :
                        gr.Key.MemberType == MemberTypes.NestedType ? "Nested types" : "Additional members"
                    );
                    yield return new TABLE { class_ = "doclist" }._(
                        gr.Select(kvp => new TR(
                            new TD { class_ = "item" }._(friendlyMemberName(kvp.Value.Member, true, false, true, true, false, false, req.BaseUrl + "/" + kvp.Key.UrlEscape(), req.BaseUrl)),
                            new TD(kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                                ? (object) new EM("This member is not documented.")
                                : interpretNodes(kvp.Value.Documentation.Element("summary").Nodes(), req))
                        ))
                    );
                }
            }
        }

        private IEnumerable<object> generateGenericTypeParameterTable(Type[] genericTypeArguments, XElement document, HttpRequest req)
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
                        new TD { class_ = "item" }._(new STRONG(gta.Name)),
                        new TD(
                            docElem == null ? (object) new EM("This type parameter is not documented.") : interpretNodes(docElem.Nodes(), req),
                            constraints == null || constraints.Length == 0 ? null :
                            constraints.Length > 0 ? new object[] { new BR(), new EM("Must be derived from:"), ' ', friendlyTypeName(constraints[0], true, true, req.BaseUrl, false, true), 
                                constraints.Skip(1).Select(c => new object[] { ", ", friendlyTypeName(constraints[0], true, true, req.BaseUrl, false, true) }) } : null
                        )
                    );
                })
            );
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
                yield return new SPAN(new EM(elem.Attribute("name").Value)) { class_ = "parameter" };
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
                return friendlyTypeName(_types[token].Type, includeNamespaces, true, req.BaseUrl, false, true);
            else if (_members.ContainsKey(token))
                return friendlyMemberName(_members[token].Member, false, false, true, false, includeNamespaces, false, req.BaseUrl + "/" + token.UrlEscape(), req.BaseUrl);
            else if (token.StartsWith("T:") && (actual = Type.GetType(token.Substring(2), false, true)) != null)
                return new SPAN(foreignTypeName(actual, includeNamespaces)) { class_ = "type", title = actual.FullName };
            else
                return new SPAN(token.Substring(2)) { class_ = "type" };
        }

        private IEnumerable<object> foreignTypeName(Type type, bool includeNamespaces)
        {
            if (includeNamespaces)
                yield return type.Namespace;
            yield return ".";
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

        private IEnumerable<object> interpretPre(XElement elem, HttpRequest req)
        {
            // Hideosly complex code to remove common indentation from each line, while allowing something like <see cref="..." /> inside a <code> element.
            // Example: suppose the input is "<code>\n    XYZ<see/>\n\n    ABC</code>". Note <see/> is an inline element.

            // Step 1: Turn all the text nodes into strings, split them at the newlines, then add the newlines back in; turn all the non-text nodes into HTML
            // Example is now: { "", E.N, "    XYZ", [code element], E.N, E.N, "    ABC" }       (E.N stands for Environment.NewLine)
            var everything = elem.Nodes().SelectMany(nod => nod is XText ? Regex.Split(((XText) nod).Value, @"\r\n|\r|\n").SelectMany(lin => new string[] { Environment.NewLine, lin.TrimEnd() }).Skip(1).Cast<object>() : interpretNode((XElement) nod, req));

            // Step 2: Split the collection at the newlines, giving a set of lines which contain strings and HTML elements
            // Example is now: { { "" }, { "    XYZ", [code element] }, { }, { "    ABC" } }
            var lines = everything.Split(el => el is string && el.Equals(Environment.NewLine));

            // Step 3: Determine the common indentation of the lines beginning with strings. Ignore empty lines as well as lines with just an empty string, but don't ignore lines beginning with a non-string object
            // Example gives commonIndentation = 4
            var commonIndentation = lines.Min(lin => lin.Any() ? lin.First() is string ? string.IsNullOrEmpty((string) lin.First()) ? (int?) null : ((string) lin.First()).TakeWhile(c => c == ' ').Count() : 0 : null);

            // If the common indentation is 0 or the entire collection is empty strings (null), put the newlines back in and return it as it is
            if (commonIndentation == null || commonIndentation.Value == 0)
                return lines.SelectMany(lin => ((object) Environment.NewLine).Concat(lin)).Skip(1);

            // Otherwise, we know that every line must start with a string (otherwise the common indentation would have been 0).
            // Put the newlines back in, and remove the common indentation from all the strings that are the first element of each line.
            // Note that the use of SubstringSafe() elegantly handles the case of lines containing just an empty string.
            // Result in the example is now: { "", E.N, "XYZ", [code element], E.N, E.N, "ABC" }
            // (We are assuming here that you never get consecutive text nodes which might together produce a larger common indentation. With current Visual Studio XML documentation generation and the .NET XML API, that doesn't appear to occur.)
            // The call to Trim() at the end removes empty strings at beginning and end.
            // Result in the example is thus: { E.N, "XYZ", [code element], "\r\n", "\r\n", "ABC" }
            return lines.SelectMany(lin => ((object) Environment.NewLine).Concat(((object) ((string) lin.First()).SubstringSafe(commonIndentation.Value)).Concat(lin.Skip(1)))).Skip(1).Trim();
        }
    }
}
