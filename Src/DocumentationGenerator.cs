using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Servers;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Json;

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
            public Dictionary<string, memberInfo> Members;

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
            public override void CleanUp(HttpResponse response, bool wasModified)
            {
                base.CleanUp(response, true);
            }
            public void SetUsername(string username)
            {
                if (username == null)
                    Action = SessionAction.Delete;
                else
                {
                    Username = username;
                    SessionModified = true;
                }
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

        private const string NoNamespaceName = "<no namespace>";

        private class memberComparer : IComparer<MemberInfo>
        {
            private memberComparer() { }
            public static readonly memberComparer Instance = new memberComparer();

            private char getTypeChar(MemberInfo member)
            {
                switch (member.MemberType)
                {
                    case MemberTypes.Constructor: return 'C';
                    case MemberTypes.Event: return 'E';
                    case MemberTypes.Field: return 'F';
                    case MemberTypes.Method: return _operators.Contains(member.Name) ? 'O' : 'M';
                    case MemberTypes.NestedType: return 'T';
                    case MemberTypes.Property: return 'P';
                }
                return 'X';
            }

            public int Compare(MemberInfo x, MemberInfo y)
            {
                var typeX = getTypeChar(x);
                var typeY = getTypeChar(y);

                if (typeX == typeY)
                {
                    var res = x.Name.CompareTo(y.Name);
                    if (res == 0 && "MO".Contains(typeX))
                    {
                        var methX = (MethodInfo) x;
                        var methY = (MethodInfo) y;
                        if (methX.IsGenericMethod && methY.IsGenericMethod)
                        {
                            var c1 = methX.GetGenericArguments().Length.CompareTo(methY.GetGenericArguments().Length);
                            if (c1 != 0)
                                return c1;
                        }
                        else if (methX.IsGenericMethod)
                            return 1;
                        else if (methY.IsGenericMethod)
                            return -1;
                        // No ‘else’ here because the first ‘if’ branch can fall through!
                        return methX.GetParameters().Length.CompareTo(methY.GetParameters().Length);
                    }
                    return res;
                }

                foreach (var c in "CMOPEFTX")
                    if (typeX == c) return -1;
                    else if (typeY == c) return 1;

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
                            var namespc = t.Namespace ?? NoNamespaceName;
                            XElement doc = e.Element("members").Elements("member").FirstOrDefault(n => n.Attribute("name").Value == typeFullName);

                            if (!_namespaces.ContainsKey(namespc))
                                _namespaces[namespc] = new namespaceInfo { Types = new SortedDictionary<string, typeInfo>() };

                            var typeinfo = new typeInfo { Type = t, Documentation = doc, Members = new Dictionary<string, memberInfo>() };

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

                            _namespaces[namespc].Types[typeFullName] = typeinfo;
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

        private FileAuthenticator _authenticator;
        private UrlResolver _resolver;

        /// <summary>Provides the HTTP request handler for the documentation.</summary>
        public HttpResponse Handle(HttpRequest request)
        {
            if (_authenticator == null && _usernamePasswordFile != null)
                _authenticator = new FileAuthenticator(_usernamePasswordFile, url => url.WithPathOnly("/").ToHref(), "the documentation");

            if (_resolver == null)
            {
                _resolver = new UrlResolver();
                foreach (var field in typeof(Css).GetFields(BindingFlags.Static | BindingFlags.Public).Where(f => f.FieldType == typeof(string)))
                {
                    var cssCode = (string) field.GetValue(null);
                    _resolver.Add(new UrlMapping(path: "/css/" + field.Name, specificPath: true, handler: req => HttpResponse.Create(cssCode, "text/css; charset=utf-8")));
                }
                _resolver.Add(new UrlMapping(path: "/q", handler: quickUrl));
                if (_usernamePasswordFile != null)
                    _resolver.Add(new UrlMapping(path: "/auth", handler: req => Session.EnableManual<docGenSession>(req, session => _authenticator.Handle(req, session.Username, session.SetUsername))));
                _resolver.Add(new UrlMapping(handler: handle));
            }

            return _resolver.Handle(request);
        }

        private HttpResponse handle(HttpRequest req)
        {
            if (req.Url.Path == "")
                return HttpResponse.Redirect(req.Url.WithPath("/"));

            return Session.EnableManual<docGenSession>(req, session =>
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
                    ns = type.Namespace ?? NoNamespaceName;
                    title = type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : typeof(Delegate).IsAssignableFrom(type) ? "Delegate: " : "Class: ";
                    title += stringSoup(friendlyTypeName(type, includeOuterTypes: true));
                    content = generateTypeDocumentation(req, _types[token].Type, _types[token].Documentation);
                }
                else if (_members.ContainsKey(token))
                {
                    member = _members[token].Member;
                    type = member.DeclaringType;
                    ns = type.Namespace;
                    title = getMemberTitle(member) + ": " + stringSoup(
                        member.MemberType == MemberTypes.Constructor ? friendlyTypeName(type, includeOuterTypes: true) :
                        member.MemberType == MemberTypes.Method ? cSharpCompatibleMethodName(member.Name) :
                        member.Name);
                    content = generateMemberDocumentation(req, _members[token].Member, _members[token].Documentation);
                }
                else if (req.Url.Path == "/")
                {
                    // If there is only one namespace, no point in asking the user to choose one. Just redirect to it
                    if (_namespaces.Count == 1)
                        return HttpResponse.Redirect(req.Url.WithPath("/" + _namespaces.First().Key));

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
                        new LINK { href = req.Url.WithPathOnly("/css/Sahifa").ToHref(), rel = "stylesheet", type = "text/css" }
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
                                        new TABLE { class_ = "legend" }._(
                                            new TR(
                                                new[] { new[] { "Class", "Struct", "Enum", "Interface", "Delegate" }, new[] { "Constructor", "Method", "Property", "Event", "Field" } }
                                                    .Select(arr => new TD { class_ = "legend" }._(arr.Select(x => new DIV { class_ = x }._(x))))
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
                _members.Where(k => k.Value.Member.MemberType != MemberTypes.NestedType && !typeof(Delegate).IsAssignableFrom(k.Value.Member.DeclaringType)),
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
                new B("Jump to:"),
                categories.Select(cat => new A { href = "#cat" + cat }._(humanReadable[cat])).InsertBetween<object>(new SPAN { class_ = "sep" }._("|"))
            );

            foreach (var group in source.GroupBy(categorize).OrderBy(gr => gr.Key))
            {
                yield return new H2(humanReadable[group.Key]) { id = "cat" + group.Key };
                yield return group.OrderBy(itemSortKey).Select(html).InsertBetween(" | ");
            }
        }

        private IEnumerable<object> friendlyTypeName(Type t, bool includeNamespaces = false, bool includeOuterTypes = false, bool variance = false, string baseUrl = null, bool inclRef = false, bool isOut = false, bool isThis = false, bool isParams = false, bool span = false, bool modifiers = false, bool namespaceSpan = false, Dictionary<Type, Type> subst = null)
        {
            if (subst != null)
                while (subst.ContainsKey(t))
                    t = subst[t];

            if (span)
            {
                yield return new SPAN { class_ = "type", title = stringSoup(friendlyTypeName(t, includeNamespaces: true, includeOuterTypes: true)) }._(
                    friendlyTypeName(t, includeNamespaces, includeOuterTypes, variance, baseUrl, inclRef, isOut, isThis, isParams, span: false, namespaceSpan: namespaceSpan, subst: subst)
                );
                yield break;
            }

            if (isThis)
                yield return "this ";

            if (t.IsByRef)
            {
                if (inclRef)
                    yield return isOut ? "out " : "ref ";
                t = t.GetElementType();
            }

            if (isParams)
                yield return "params ";

            if (t.IsArray)
            {
                yield return friendlyTypeName(t.GetElementType(), includeNamespaces, includeOuterTypes, variance, span: span, namespaceSpan: namespaceSpan, subst: subst);
                yield return "[]";
                yield break;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                yield return friendlyTypeName(t.GetGenericArguments()[0], includeNamespaces, includeOuterTypes, variance, span: span, namespaceSpan: namespaceSpan, subst: subst);
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
                    yield return friendlyTypeName(t.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance, baseUrl: baseUrl, span: span, namespaceSpan: namespaceSpan, subst: subst);
                    yield return ".";
                }
                else if (includeNamespaces && !t.IsGenericParameter)
                {
                    var arr = t.Namespace.NullOr(ns => new object[] { ns, "." });
                    yield return namespaceSpan ? new SPAN { class_ = "namespace" }._(arr) : (object) arr;
                }

                // Determine whether this type has its own generic type parameters.
                // This is different from being a generic type: a nested type of a generic type is automatically a generic type too, even though it doesn't have generic parameters of its own.
                var hasGenericTypeParameters = t.Name.Contains('`');

                string ret = t.IsGenericParameter ? t.Name : hasGenericTypeParameters ? t.Name.Remove(t.Name.IndexOf('`')) : t.Name;
                if (baseUrl != null && !t.IsGenericParameter && _types.ContainsKey(getTypeFullName(t)))
                    yield return new A { href = baseUrl + "/" + getTypeFullName(t).UrlEscape() }._(ret);
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
                        yield return friendlyTypeName(ga, includeNamespaces, includeOuterTypes, variance, baseUrl, inclRef, span: span, namespaceSpan: namespaceSpan, subst: subst);
                    }
                    yield return ">";
                }
            }
        }

        private object friendlyMemberName(MemberInfo m, bool returnType = false, bool containingType = false, bool parameterTypes = false, bool parameterNames = false, bool parameterDefaultValues = false, bool omitGenericTypeParameters = false, bool namespaces = false, bool variance = false, bool indent = false, string url = null, string baseUrl = null, bool stringOnly = false, bool modifiers = false, Dictionary<Type, Type> subst = null)
        {
            if (m.MemberType == MemberTypes.NestedType)
                return friendlyTypeName((Type) m, includeNamespaces: namespaces, includeOuterTypes: containingType, variance: variance, baseUrl: baseUrl, span: !stringOnly, modifiers: modifiers, subst: subst);

            if (m.MemberType == MemberTypes.Constructor || m.MemberType == MemberTypes.Method)
            {
                var f = friendlyMethodName((MethodBase) m, returnType, containingType, parameterTypes, parameterNames, parameterDefaultValues, omitGenericTypeParameters, namespaces, variance, indent, url, baseUrl, stringOnly: stringOnly, modifiers: modifiers, subst: subst);
                return stringOnly ? (object) f : new SPAN { class_ = m.MemberType.ToString(), title = stringSoup(friendlyMemberName(m, true, true, true, true, true, stringOnly: true, modifiers: true)) }._(f);
            }

            var arr = Ut.NewArray<object>(
                returnType && m.MemberType == MemberTypes.Property ? new object[] { friendlyTypeName(((PropertyInfo) m).PropertyType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst), " " } :
                returnType && m.MemberType == MemberTypes.Event ? new object[] { friendlyTypeName(((EventInfo) m).EventHandlerType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst), " " } :
                returnType && m.MemberType == MemberTypes.Field ? new object[] { friendlyTypeName(((FieldInfo) m).FieldType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst), " " } : null,
                containingType ? friendlyTypeName(m.DeclaringType, includeNamespaces: namespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst) : null,
                containingType ? "." : null,
                m.MemberType == MemberTypes.Property
                ? (object) friendlyPropertyName((PropertyInfo) m, parameterTypes, parameterNames, namespaces, indent, url, baseUrl, stringOnly, subst)
                    : stringOnly ? (object) m.Name : new STRONG { class_ = "member-name" }._(url == null ? (object) m.Name : new A { href = url }._(m.Name))
            );
            return stringOnly ? (object) stringSoup(arr) : new SPAN { class_ = m.MemberType.ToString(), title = stringSoup(friendlyMemberName(m, true, true, true, true, true, stringOnly: true)) }._(arr);
        }

        private IEnumerable<object> friendlyMethodName(MethodBase m, bool returnType = false, bool containingType = false, bool parameterTypes = false, bool parameterNames = false, bool parameterDefaultValues = false, bool omitGenericTypeParameters = false, bool includeNamespaces = false, bool variance = false, bool indent = false, string url = null, string baseUrl = null, bool isDelegate = false, bool stringOnly = false, bool modifiers = false, Dictionary<Type, Type> subst = null)
        {
            if (isDelegate)
                yield return "delegate ";
            if (modifiers)
            {
                if (m.IsPublic)
                    yield return "public ";
                else if (m.IsFamily)
                    yield return "protected ";
                else if (m.IsFamilyOrAssembly)
                    yield return "protected internal ";
                // The following should never occur, but add for completeness...
                else if (m.IsAssembly)
                    yield return "internal ";
                else if (m.IsFamilyAndAssembly)
                    yield return "proternal ";  // no C# equivalent
                else if (m.IsPrivate)
                    yield return "private ";

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
                    yield return friendlyTypeName(meth.ReturnType, includeNamespaces: includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst);
                    yield return " ";
                }
            }
            if ((m.MemberType == MemberTypes.Constructor || isDelegate) && url != null)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: isDelegate, span: false, subst: subst) : new STRONG { class_ = "member-name" }._(new A { href = url }._(friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: isDelegate, span: true, subst: subst)));
            else if (isDelegate)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance, subst: subst) : new STRONG { class_ = "member-name" }._(friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, variance: variance, baseUrl: baseUrl, span: true, subst: subst));
            else if (containingType)
                yield return friendlyTypeName(m.DeclaringType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, span: !stringOnly, subst: subst);
            else if (m.MemberType == MemberTypes.Constructor)
                yield return stringOnly ? (object) friendlyTypeName(m.DeclaringType, includeNamespaces, subst: subst) : new STRONG { class_ = "member-name" }._(friendlyTypeName(m.DeclaringType, includeNamespaces, baseUrl: baseUrl, span: true, subst: subst));
            if (!stringOnly && !indent && (m.IsGenericMethod || (parameterNames && m.GetParameters().Any()))) yield return new WBR();
            if (m.MemberType != MemberTypes.Constructor && !isDelegate)
            {
                if (containingType) yield return ".";
                object f = cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, includeOuterTypes: true, span: !stringOnly, subst: subst));
                if (url != null) f = new A { href = url }._(f);
                if (!stringOnly) f = new STRONG { class_ = "member-name" }._(f);
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
                    var f = Ut.NewArray<object>(
                        parameterTypes ? friendlyTypeName(p.ParameterType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, inclRef: true, isOut: p.IsOut, isThis: first && m.IsDefined<ExtensionAttribute>(), isParams: p.IsDefined<ParamArrayAttribute>(), span: !stringOnly, subst: subst) : null,
                        parameterTypes && parameterNames ? " " : null,
                        parameterNames ? (stringOnly || !parameterTypes ? (object) p.Name : new STRONG(p.Name)) : null,
                        parameterDefaultValues && p.Attributes.HasFlag(ParameterAttributes.HasDefault) ? friendlyDefaultValue(p.DefaultValue, baseUrl, stringOnly) : null
                    );
                    first = false;
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

        private static string[] _operators = Ut.NewArray(
            "op_Implicit", "op_Explicit",
            "op_UnaryPlus", "op_Addition", "op_UnaryNegation", "op_Subtraction",
            "op_LogicalNot", "op_OnesComplement", "op_Increment", "op_Decrement", "op_True", "op_False",
            "op_Multiply", "op_Division", "op_Modulus", "op_BitwiseAnd", "op_BitwiseOr", "op_ExclusiveOr", "op_LeftShift", "op_RightShift",
            "op_Equality", "op_Inequality", "op_LessThan", "op_GreaterThan", "op_LessThanOrEqual", "op_GreaterThanOrEqual"
        );

        private object cSharpCompatibleMethodName(string methodName, object returnType = null)
        {
            switch (methodName)
            {
                case "op_Implicit": return returnType == null ? (object) "implicit operator" : new object[] { "implicit operator ", returnType };
                case "op_Explicit": return returnType == null ? (object) "explicit operator" : new object[] { "explicit operator ", returnType };

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

        private IEnumerable<object> friendlyPropertyName(PropertyInfo property, bool parameterTypes, bool parameterNames, bool includeNamespaces, bool indent, string url, string baseUrl, bool stringOnly, Dictionary<Type, Type> subst)
        {
            var prms = property.GetIndexParameters();
            if (prms.Length > 0)
            {
                yield return stringOnly ? (object) "this" : new STRONG { class_ = "member-name" }._(url == null ? (object) "this" : new A { href = url }._("this"));
                yield return indent ? "[\n    " : "[";
                if (!indent && !stringOnly) yield return new WBR();
                bool first = true;
                foreach (var p in prms)
                {
                    if (!first) yield return indent ? ",\n    " : ", ";
                    first = false;
                    var arr = Ut.NewArray<object>(
                        parameterTypes && p.IsOut ? "out " : null,
                        parameterTypes ? friendlyTypeName(p.ParameterType, includeNamespaces, includeOuterTypes: true, baseUrl: baseUrl, inclRef: !p.IsOut, span: !stringOnly, subst: subst) : null,
                        parameterTypes && parameterNames ? " " : null,
                        parameterNames ? (stringOnly ? (object) p.Name : new STRONG(p.Name)) : null
                    );
                    yield return stringOnly ? (object) arr : new SPAN { class_ = "parameter" }._(arr);
                }
                yield return indent ? "\n]" : "]";
            }
            else
                yield return stringOnly ? (object) property.Name : new STRONG { class_ = "member-name" }._(url == null ? (object) property.Name : new A { href = url }._(property.Name));
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

        private string documentationCompatibleMemberName(MemberInfo m, Type reflectedType = null, Dictionary<Type, Type> genericSubstitutionsForHidingDetection = null)
        {
            StringBuilder sb = new StringBuilder();
            if (m.MemberType == MemberTypes.Method || m.MemberType == MemberTypes.Constructor)
            {
                MethodBase mi = m as MethodBase;
                sb.Append("M:");
                if (genericSubstitutionsForHidingDetection == null)
                {
                    var declaringType = mi.DeclaringType;
                    if (declaringType.IsGenericType && !declaringType.IsGenericTypeDefinition)
                        declaringType = declaringType.GetGenericTypeDefinition();
                    sb.Append(declaringType.FullName.Replace("+", "."));
                    sb.Append(".");
                }
                sb.Append(m.MemberType == MemberTypes.Method ? mi.Name : "#ctor");
                if (mi.IsGenericMethod)
                {
                    sb.Append("``");
                    sb.Append(mi.GetGenericArguments().Count());
                }
                appendParameterTypes(sb, mi.GetParameters(), reflectedType ?? mi.ReflectedType, mi as MethodInfo, genericSubstitutionsForHidingDetection);
            }
            else if (m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Event)
            {
                sb.Append(m.MemberType == MemberTypes.Field ? "F:" : "E:");
                if (genericSubstitutionsForHidingDetection == null)
                {
                    sb.Append(m.DeclaringType.FullName.Replace("+", "."));
                    sb.Append(".");
                }
                sb.Append(m.Name);
            }
            else if (m.MemberType == MemberTypes.Property)
            {
                var prop = (PropertyInfo) m;
                sb.Append("P:");
                if (genericSubstitutionsForHidingDetection == null)
                {
                    sb.Append(prop.DeclaringType.FullName.Replace("+", "."));
                    sb.Append(".");
                }
                sb.Append(prop.Name);
                if (m.MemberType == MemberTypes.Property && prop.GetIndexParameters().Length > 0)
                    appendParameterTypes(sb, prop.GetIndexParameters(), prop.ReflectedType);
            }
            else if (m.MemberType == MemberTypes.NestedType)
            {
                if (genericSubstitutionsForHidingDetection == null)
                    sb.Append(getTypeFullName((Type) m));
                else
                    sb.Append(m.Name);
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
                sb.Append("~" + stringifyParameterType(b.ReturnType, b.ReflectedType, b, genericSubstitutionsForHidingDetection));
            }
            return sb.ToString();
        }

        private void appendParameterTypes(StringBuilder sb, ParameterInfo[] parameters, Type type, MethodInfo method = null, Dictionary<Type, Type> genericSubstitutions = null)
        {
            bool first = true;
            foreach (var param in parameters)
            {
                sb.Append(first ? "(" : ",");
                first = false;
                sb.Append(stringifyParameterType(param.ParameterType, type, method, genericSubstitutions));
            }
            if (!first) sb.Append(")");
        }

        private string stringifyParameterType(Type parameterType, Type type, MethodInfo method, Dictionary<Type, Type> genericSubstitutions)
        {
            if (parameterType.IsByRef)
                return stringifyParameterType(parameterType.GetElementType(), type, method, genericSubstitutions) + "@";

            if (parameterType.IsArray)
                return stringifyParameterType(parameterType.GetElementType(), type, method, genericSubstitutions) + "[]";

            if (!parameterType.IsGenericType && !parameterType.IsGenericParameter)
                return parameterType.FullName.Replace("+", ".");

            if (parameterType.IsGenericParameter)
            {
                Type output;
                if (genericSubstitutions != null && genericSubstitutions.TryGetValue(parameterType, out output))
                    return stringifyParameterType(output, type, method, genericSubstitutions);

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
                throw new InternalErrorException("Parameter type is a generic type, but its generic argument is neither in the type nor method definition.");
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
                    constructName += fullName.Substring(0, m.Index) + "{" + genericArguments.Take(num).Select(g => stringifyParameterType(g, type, method, genericSubstitutions)).JoinString(",") + "}";
                    fullName = fullName.Substring(m.Index + m.Length);
                    genericArguments = genericArguments.Skip(num);
                }
                return constructName + fullName;
            }

            throw new InternalErrorException("I totally don't know what to do with this parameter type.");
        }

        private bool isPropertyGetterOrSetter(MemberInfo member)
        {
            if (member.MemberType != MemberTypes.Method)
                return false;
            if (!member.Name.StartsWith("get_") && !member.Name.StartsWith("set_"))
                return false;
            string partName = member.Name.Substring(4);
            return member.DeclaringType.GetMembers(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Any(m => m.MemberType == MemberTypes.Property && m.Name == partName);
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
                    return ((Type) member).IsNestedPublic;

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
            return new DIV { class_ = "type indent" }._(new DIV { class_ = cssClass }._(friendlyTypeName(typeinfo.Type, span: true, baseUrl: req.Url.WithPathOnly("").ToHref())),
                selectedMember != null && !typeof(Delegate).IsAssignableFrom(typeinfo.Type) && isMemberInside(selectedMember, typeinfo.Type)
                    ? typeinfo.Members.Where(mkvp => isPublic(mkvp.Value.Member))
                        .OrderBy(mkvp => mkvp.Value.Member, memberComparer.Instance)
                        .Select(mkvp =>
                        {
                            if (mkvp.Value.Member.MemberType == MemberTypes.NestedType)
                                return generateTypeBullet(getTypeFullName((Type) mkvp.Value.Member), selectedMember, req);
                            string css = mkvp.Value.Member.MemberType.ToString() + " member indent";
                            if (mkvp.Value.Documentation == null) css += " missing";
                            if (mkvp.Value.Member == selectedMember) css += " highlighted";
                            return new DIV { class_ = css }._(friendlyMemberName(mkvp.Value.Member, parameterNames: true, omitGenericTypeParameters: true, baseUrl: req.Url.WithPathOnly("").ToHref(), url: req.Url.WithPathOnly("/" + mkvp.Key.UrlEscape()).ToHref()));
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
            yield return new H1 { class_ = "namespace-heading" }._("Namespace: ", new SPAN { class_ = "namespace" }._(namespaceName));
            yield return new DIV { class_ = "innercontent" }._(
                new TABLE { class_ = "doclist" }._(
                    _namespaces[namespaceName].Types.Where(t => !t.Value.Type.IsNested).Select(kvp => new TR(
                        new TD { class_ = "type" + (kvp.Value.Documentation == null ? " missing" : "") }._(
                            new DIV { class_ = kvp.Value.GetTypeCssClass() }._(
                                friendlyTypeName(kvp.Value.Type, span: true, baseUrl: req.Url.WithPathOnly("").ToHref())
                            )
                        ),
                        new TD(
                            kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                                ? (object) new EM("This type is not documented.")
                                : interpretNodes(kvp.Value.Documentation.Element("summary").Nodes(), req),
                            kvp.Value.Documentation == null || kvp.Value.Documentation.Element("remarks") == null
                                ? null
                                : new object[] { " ", new SPAN { class_ = "extra" }._("(see also remarks)") }
                        )
                    ))
                )
            );
        }

        private IEnumerable<object> generateMemberDocumentation(HttpRequest req, MemberInfo member, XElement documentation)
        {
            yield return new H1(
                getMemberTitle(member), ": ",
                friendlyMemberName(member, returnType: true, parameterTypes: true)
            );

            yield return new DIV { class_ = "innercontent" }._(generateMemberDocumentationInner(req, member, documentation));
        }

        private string getMemberTitle(MemberInfo member)
        {
            bool isStatic =
                member.MemberType == MemberTypes.Field && (member as FieldInfo).IsStatic ||
                member.MemberType == MemberTypes.Method && (member as MethodInfo).IsStatic ||
                member.MemberType == MemberTypes.Property && (member as PropertyInfo).GetGetMethod().IsStatic;
            return
                member.MemberType == MemberTypes.Constructor ? "Constructor" :
                member.MemberType == MemberTypes.Event ? "Event" :
                member.MemberType == MemberTypes.Field ? (isStatic ? "Static field" : "Field") :
                member.MemberType == MemberTypes.Method && isStatic && _operators.Contains(member.Name) ? "Operator" :
                member.MemberType == MemberTypes.Method ? (member.IsDefined<ExtensionAttribute>() ? "Extension method" : isStatic ? "Static method" : "Method") :
                member.MemberType == MemberTypes.Property ? (isStatic ? "Static property" : "Property") : "Member";
        }

        private IEnumerable<object> generateMemberDocumentationInner(HttpRequest req, MemberInfo member, XElement documentation)
        {
            yield return new UL(
                new LI("Declared in: ", friendlyTypeName(member.DeclaringType, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true, namespaceSpan: true)),
                formatMemberExtraInfoItems(req, member, true, null, null, null)
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
                if (method.IsGenericMethod)
                    yield return generateGenericTypeParameterTable(req, documentation, method.GetGenericArguments());
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
        }

        private IEnumerable<object> generateTypeDocumentation(HttpRequest req, Type type, XElement documentation)
        {
            bool isDelegate = typeof(Delegate).IsAssignableFrom(type);

            yield return new H1(
                type.IsNested
                    ? (isDelegate ? "Nested delegate: " : type.IsEnum ? "Nested enum: " : type.IsValueType ? "Nested struct: " : type.IsInterface ? "Nested interface: " : (type.IsAbstract && type.IsSealed) ? "Nested static class: " : type.IsAbstract ? "Nested abstract class: " : type.IsSealed ? "Nested sealed class: " : "Nested class: ")
                    : (isDelegate ? "Delegate: " : type.IsEnum ? "Enum: " : type.IsValueType ? "Struct: " : type.IsInterface ? "Interface: " : (type.IsAbstract && type.IsSealed) ? "Static class: " : type.IsAbstract ? "Abstract class: " : type.IsSealed ? "Sealed class: " : "Class: "),
                friendlyTypeName(type, includeNamespaces: true, includeOuterTypes: true, variance: true, span: true, namespaceSpan: true)
            );

            yield return new DIV { class_ = "innercontent" }._(generateTypeDocumentationInner(req, type, documentation));
        }

        private IEnumerable<object> generateTypeDocumentationInner(HttpRequest req, Type type, XElement documentation)
        {
            bool isDelegate = typeof(Delegate).IsAssignableFrom(type);

            yield return new UL { class_ = "typeinfo" }._(
                new LI("Assembly: ", new SPAN { class_ = "assembly" }._(type.Assembly.FullName)),
                new LI("Namespace: ", new A { class_ = "namespace", href = req.Url.WithPathOnly("/" + (type.Namespace ?? NoNamespaceName).UrlEscape()).ToHref() }._(type.Namespace)),
                type.IsNested ? new LI("Declared in: ", friendlyTypeName(type.DeclaringType, includeNamespaces: true, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true, namespaceSpan: true)) : null,
                inheritsFrom(type, req),
                implementsInterfaces(type, req),
                type.IsInterface ? implementedBy(type, req) : derivedTypes(type, req),
                type.IsEnum ? new LI("Underlying integer type: ", new CODE(type.GetEnumUnderlyingType().FullName)) : null
            );

            MethodInfo delegateInvokeMethod = null;
            if (isDelegate)
            {
                delegateInvokeMethod = type.GetMethod("Invoke");
                yield return new H2("Declaration");
                yield return new PRE(friendlyMethodName(delegateInvokeMethod, returnType: true, parameterTypes: true, parameterNames: true, includeNamespaces: true, variance: true, indent: true, baseUrl: req.Url.WithPathOnly("").ToHref(), isDelegate: true));
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
                    if (delegateInvokeMethod.GetParameters().Any())
                        yield return generateParameterDocumentation(req, delegateInvokeMethod, documentation);

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
                foreach (var group in new { Type = type, Substitutions = new Dictionary<Type, Type>() }
                    // Find all the base types with their generic type parameters
                    .SelectChain(ti => ti.Type.BaseType.NullOr(bt => bt.IsGenericType
                        ? new { Type = bt.GetGenericTypeDefinition(), Substitutions = ti.Substitutions.Concat(bt.GetGenericTypeDefinition().GetGenericArguments().Select((ga, gai) => Ut.KeyValuePair(ga, bt.GetGenericArguments()[gai]))).ToDictionary() }
                        : new { Type = bt, Substitutions = new Dictionary<Type, Type>() }))
                    // Remove types that are not in our scope
                    .Where(ti => _types.ContainsKey(getTypeFullName(ti.Type)))
                    // Get all the members of this type and all base types
                    .SelectMany(ti => _types[getTypeFullName(ti.Type)].Members.Select(m => new { InheritedFrom = ti.Type, Substitutions = ti.Substitutions, MemberName = m.Key, Member = m.Value.Member, Documentation = m.Value.Documentation }))
                    // Filter out internal members and inherited constructors
                    .Where(inf => isPublic(inf.Member) && (inf.InheritedFrom == type || inf.Member.MemberType != MemberTypes.Constructor))
                    // For chains of virtual overrides, only use the least derived member (the “base” method/property/event)
                    .GroupBy(mbr => mbr.Member.IfType((MethodInfo m) => m.GetBaseDefinition(), (PropertyInfo p) => p.GetBaseDefinition(), (EventInfo e) => e.GetBaseDefinition(), m => m))
                    .Select(g => g.First())
                    // Find out which inherited members are hidden by less derived members of the same signature
                    .GroupBy(mbr => documentationCompatibleMemberName(mbr.Member, type, mbr.Substitutions))
                    .Select(gr => new { LeastDerived = gr.First(), Hides = gr.Skip(1).Select(hide => new hideInfo(hide.Member, hide.MemberName, hide.Substitutions)).ToArray() })
                    .Select(gr => new { gr.LeastDerived.Documentation, gr.LeastDerived.InheritedFrom, gr.LeastDerived.Member, gr.LeastDerived.MemberName, gr.LeastDerived.Substitutions, gr.Hides })
                    // Sorting
                    .OrderBy(inf => inf.Member, memberComparer.Instance)
                    // Group by the sections we want on the page (constructors, events, fields, methods, operators, properties, nested types, etc.etc.)
                    .GroupBy(inf => new { MemberType = inf.Member.MemberType, IsStatic = isStatic(inf.Member), IsOperator = inf.Member.MemberType == MemberTypes.Method && _operators.Contains(inf.Member.Name) }))
                {
                    var isEnumValues = group.Key.MemberType == MemberTypes.Field && group.Key.IsStatic && type.IsEnum;

                    yield return new H2(
                        group.Key.MemberType == MemberTypes.Constructor ? "Constructors" :
                        group.Key.MemberType == MemberTypes.Event ? "Events" :
                        isEnumValues ? "Enum values" :
                        group.Key.MemberType == MemberTypes.Field && group.Key.IsStatic ? "Static fields" :
                        group.Key.MemberType == MemberTypes.Field ? "Instance fields" :
                        group.Key.IsOperator ? "Operators" :
                        group.Key.MemberType == MemberTypes.Method && group.Key.IsStatic ? "Static methods" :
                        group.Key.MemberType == MemberTypes.Method ? "Instance methods" :
                        group.Key.MemberType == MemberTypes.Property && group.Key.IsStatic ? "Static properties" :
                        group.Key.MemberType == MemberTypes.Property ? "Instance properties" :
                        group.Key.MemberType == MemberTypes.NestedType ? "Nested types" : "Additional members"
                    );

                    yield return new TABLE { class_ = "doclist" }._(group
                        .GroupConsecutive((inf1, inf2) => summaryIsMergable(inf1.Documentation, inf2.Documentation))
                        .SelectMany(gr => gr.Select((inf, index) => new TR(
                            inf.Member.MemberType == MemberTypes.Constructor || inf.Member.MemberType == MemberTypes.NestedType || isEnumValues ? null :
                                new TD { class_ = "membertype" }._(
                                    friendlyTypeName(
                                        inf.Member is EventInfo ? ((EventInfo) inf.Member).EventHandlerType :
                                        inf.Member is FieldInfo ? ((FieldInfo) inf.Member).FieldType :
                                        inf.Member is MethodInfo ? ((MethodInfo) inf.Member).ReturnType :
                                        inf.Member is PropertyInfo ? ((PropertyInfo) inf.Member).PropertyType : null,
                                        includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), span: true, subst: inf.Substitutions
                                    )
                                ),
                            new TD { class_ = "member" }._(
                                new DIV { class_ = "withicon " + (inf.Member.MemberType == MemberTypes.NestedType ? _types[inf.MemberName].GetTypeCssClass() : inf.Member.MemberType.ToString()) }._(
                                    friendlyMemberName(inf.Member, parameterTypes: true, parameterNames: true, parameterDefaultValues: true, url: req.Url.WithPathOnly("/" + inf.MemberName.UrlEscape()).ToHref(), baseUrl: req.Url.WithPathOnly("").ToHref(), subst: inf.Substitutions)
                                ),
                                formatMemberExtraInfo(req, inf.Member, false, inf.InheritedFrom == type ? null : inf.InheritedFrom, inf.Substitutions, inf.Hides)
                            ),
                            isEnumValues ? new TD { class_ = "numeric" }._(((FieldInfo) inf.Member).GetRawConstantValue()) : null,
                            index > 0 ? null : new TD { class_ = "documentation", rowspan = gr.Count }._(
                                inf.Documentation == null || inf.Documentation.Element("summary") == null
                                    ? (object) new EM(gr.Count > 1 ? "These members are not documented." : "This member is not documented.")
                                    : interpretNodes(inf.Documentation.Element("summary").Nodes(), req),
                                inf.Documentation == null || inf.Documentation.Element("remarks") == null
                                    ? null
                                    : new object[] { " ", new SPAN { class_ = "extra" }._("(see also remarks)") }
                            )
                        )))
                    );
                }
            }
        }

        private sealed class hideInfo
        {
            public MemberInfo Member { get; private set; }
            public string MemberName { get; private set; }
            public Dictionary<Type, Type> Substitutions { get; private set; }
            public hideInfo(MemberInfo member, string memberName, Dictionary<Type, Type> substitutions)
            {
                Member = member;
                MemberName = memberName;
                Substitutions = substitutions;
            }
        }

        private IEnumerable<object> generateParameterDocumentation(HttpRequest req, MethodBase method, XElement methodDocumentation)
        {
            var parameters = method.GetParameters();
            var parameterInfos = Enumerable.Range(0, parameters.Length).Select(i => new
            {
                Parameter = parameters[i],
                Documentation = methodDocumentation == null ? null : methodDocumentation.Elements("param")
                        .FirstOrDefault(xe => xe.Attribute("name") != null && xe.Attribute("name").Value == parameters[i].Name)
            }).ToArray();

            if (parameterInfos.All(pi => pi.Documentation == null))
                yield break;

            yield return new H2("Parameters");
            yield return new TABLE { class_ = "doclist" }._(
                parameterInfos.Select((pi, index) =>
                {
                    return new TR(
                        new TD { class_ = "membertype" }._(
                            friendlyTypeName(pi.Parameter.ParameterType, includeOuterTypes: true, baseUrl: req.Url.WithPathOnly("").ToHref(), inclRef: true, isOut: pi.Parameter.IsOut, isThis: index == 0 && method.IsDefined<ExtensionAttribute>(), span: true)
                        ),
                        new TD { class_ = "member" }._(
                            new STRONG { class_ = "member-name parameter" }._(pi.Parameter.Name)
                        ),
                        new TD { class_ = "documentation" }._(pi.Documentation == null
                            ? (object) new EM("This parameter is not documented.")
                            : interpretNodes(pi.Documentation.Nodes(), req))
                    );
                })
            );
        }

        private bool summaryIsMergable(XElement doc1, XElement doc2)
        {
            if (doc1 == null)
                return doc2 == null;
            else if (doc2 == null)
                return false;
            if (doc1.Element("summary") == null)
                return doc2.Element("summary") == null;
            else if (doc2.Element("summary") == null)
                return false;

            // We do not compare the /content/ of remarks tags, but we want to merge summaries only if both have or both don’t have a remarks tag
            if ((doc1.Element("remarks") == null) != (doc2.Element("remarks") == null))
                return false;

            return XNode.DeepEquals(doc1.Element("summary"), doc2.Element("summary"));
        }

        private IEnumerable<object> formatMemberExtraInfo(HttpRequest req, MemberInfo member, bool markInterfaceMethods, Type inheritedFrom, Dictionary<Type, Type> subst, hideInfo[] hides)
        {
            var listItems = formatMemberExtraInfoItems(req, member, markInterfaceMethods, inheritedFrom, subst, hides).ToList();
            if (listItems.Count > 0)
                yield return new UL { class_ = "extra" }._(listItems);
        }

        private IEnumerable<object> formatMemberExtraInfoItems(HttpRequest req, MemberInfo member, bool markInterfaceMethods, Type inheritedFrom, Dictionary<Type, Type> subst, hideInfo[] hides)
        {
            string baseUrl = req.Url.WithPathOnly("").ToHref();

            if (inheritedFrom != null)
                yield return new LI("Inherited from ", friendlyTypeName(inheritedFrom, includeOuterTypes: true, baseUrl: baseUrl, span: true, subst: subst));

            if (hides != null)
                foreach (var hide in hides)
                    yield return new LI("Hides ", friendlyMemberName(hide.Member, containingType: true, parameterTypes: true, url: req.Url.WithPathOnly("/" + hide.MemberName.UrlEscape()).ToHref(), baseUrl: baseUrl, subst: hide.Substitutions));

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
                    string url = null;
                    var baseDefinitionWithoutGenerics = baseDefinition.IfType(
                        // Dirty hack, but simplest way I could find to retrieve the uninstantiated MethodInfo
                        (MethodInfo mi) => mi.ContainsGenericParameters ? MethodInfo.GetMethodFromHandle(mi.MethodHandle, mi.DeclaringType.GetGenericTypeDefinition().TypeHandle) : mi,
                        mi => mi
                    );
                    var dcmn = documentationCompatibleMemberName(baseDefinitionWithoutGenerics);
                    if (_members.ContainsKey(dcmn))
                        url = req.Url.WithPathOnly("/" + dcmn.UrlEscape()).ToHref();
                    yield return new LI(new object[] { "Overrides: ", friendlyMemberName(baseDefinition, containingType: true, parameterTypes: true, url: url, baseUrl: baseUrl) });
                    if (method.IsFinal)
                        yield return new LI("Sealed");
                    showVirtual = false;
                }

                foreach (var interf in method.ReflectedType.GetInterfaces())
                {
                    var map = method.ReflectedType.GetInterfaceMap(interf);
                    var index = map.TargetMethods.IndexOf(method);
                    if (index != -1)
                    {
                        string url = null;
                        var interfaceMember =
                            (MemberInfo) interf.GetProperties().FirstOrDefault(p => p.GetGetMethod() == map.InterfaceMethods[index] || p.GetSetMethod() == map.InterfaceMethods[index]) ??
                            (MemberInfo) interf.GetEvents().FirstOrDefault(e => e.GetAddMethod() == map.InterfaceMethods[index] || e.GetRemoveMethod() == map.InterfaceMethods[index]) ??
                            map.InterfaceMethods[index];
                        var interfaceMemberDefinition = findMemberDefinition(interfaceMember);
                        var dcmn = documentationCompatibleMemberName(interfaceMemberDefinition);
                        if (_members.ContainsKey(dcmn))
                            url = req.Url.WithPathOnly("/" + dcmn.UrlEscape()).ToHref();
                        yield return new LI("Implements: ", friendlyMemberName(interfaceMember, containingType: true, parameterTypes: true, url: url, baseUrl: baseUrl));
                        showVirtual = showVirtual && !method.IsFinal;
                    }
                }

                if (method.IsAbstract)
                    yield return new LI("Abstract");
                else if (showVirtual)
                    yield return new LI("Virtual");
            }
        }

        private MemberInfo findMemberDefinition(MemberInfo member)
        {
            var method = member as MethodInfo;
            if (method != null)
            {
                if (method.IsGenericMethod && !method.IsGenericMethodDefinition)
                    return findMemberDefinition(method.GetGenericMethodDefinition());
                if (!method.DeclaringType.IsGenericType || method.DeclaringType.IsGenericTypeDefinition)
                    return method;
                var def = method.DeclaringType.GetGenericTypeDefinition();
                return def.GetMethods((method.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic) | (method.IsStatic ? BindingFlags.Static : BindingFlags.Instance))
                    .FirstOrDefault(m => sameExcept(m, method, def.GetGenericArguments(), method.DeclaringType.GetGenericArguments()));
            }
            if (!member.DeclaringType.IsGenericType || member.DeclaringType.IsGenericTypeDefinition)
                return member;

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
                        new TD { class_ = "member" }._(new STRONG { class_ = "generic-parameter member-name" }._(gta.Name), formatGenericConstraints(req, constraints, gta.GenericParameterAttributes)),
                        new TD { class_ = "documentation" }._(docElem == null ? (object) new EM("This type parameter is not documented.") : interpretNodes(docElem.Nodes(), req))
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
                return friendlyMemberName(_members[token].Member, containingType: true, parameterTypes: true, namespaces: includeNamespaces, url: req.Url.WithPathOnly("/" + token.UrlEscape()).ToHref(), baseUrl: req.Url.WithPathOnly("").ToHref());
            else if (token.StartsWith("T:") && (actual = Type.GetType(token.Substring(2), throwOnError: false, ignoreCase: true)) != null)
                return new SPAN { title = actual.FullName }._(foreignTypeName(actual, includeNamespaces));
            else if (token.StartsWith("T:"))
                return new SPAN { title = token.Substring(2) }._(foreignTypeName(token.Substring(2), includeNamespaces));
            else if (token.StartsWith("M:") || token.StartsWith("P:") || token.StartsWith("E:") || token.StartsWith("F:"))
            {
                return new SPAN
                {
                    class_ = token.StartsWith("M:") ? "Method" :
                        token.StartsWith("P:") ? "Property" :
                        token.StartsWith("E:") ? "Event" :
                        token.StartsWith("F:") ? "Field" : null
                }._(
                    CrefParser.Parse(token.Substring(2))
                        .GetHtml(Assumption.Member,
                            member => friendlyMemberName(member, containingType: true, parameterTypes: true),
                            type => friendlyTypeName(type, includeOuterTypes: true, inclRef: true, span: true))
                );
            }
            else
                return new SPAN { title = token.Substring(2) }._("[Unrecognized cref attribute]");
        }

        private object foreignTypeName(Type type, bool includeNamespaces)
        {
            return new SPAN { class_ = "type" }._(foreignTypeNameInner(type, includeNamespaces));
        }

        private object foreignTypeName(string type, bool includeNamespaces)
        {
            return new SPAN { class_ = "type" }._(foreignTypeNameInner(type, includeNamespaces));
        }

        private IEnumerable<object> foreignTypeNameInner(Type type, bool includeNamespaces)
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

        private IEnumerable<object> foreignTypeNameInner(string type, bool includeNamespaces)
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
                yield return numGenerics == 1 ? "T" : Enumerable.Range(1, numGenerics).Select(i => "T" + i).JoinString(", ");
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
