﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Servers;
using RT.TagSoup.HtmlTags;
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

        private SortedDictionary<string, namespaceInfo> _namespaces;
        private SortedDictionary<string, typeInfo> _types;
        private SortedDictionary<string, memberInfo> _members;

        private static string _css = @"
            body { font-family: ""Segoe UI"", ""Verdana"", sans-serif; font-size: 11pt; margin: .5em; }
            .namespace { font-weight: normal; color: #888; }
            a.namespace { color: #00e; }
            a.namespace:visited { color: #551a8b; }
            .Method { font-weight: normal; }
            .sidebar { font-size: small; }
            .sidebar li { margin: 0; }
            .sidebar div.type { padding-left: 2em; }
            .sidebar div.member { padding-left: 2.5em; text-indent: -2.5em; }
            .sidebar div.type > div { font-weight: normal; }
            .sidebar div.type > div.line { font-weight: bold; padding-left: 0.5em; text-indent: -2.5em; }
            .sidebar div.type span.typeicon, .sidebar div.member span.icon { display: inline-block; width: 1.5em; margin-right: 0.5em; text-indent: 0; text-align: center; color: #000; -moz-border-radius: 0.7em 0.7em 0.7em 0.7em; }
            .sidebar div.legend div.type, .sidebar div.legend div.member { padding-left: 0; }

            span.icon, span.typeicon { font-size: smaller; }

            .sidebar div.Constructor.member span.icon { background-color: #bfb; border: 2px solid #bfb; }
            .sidebar div.Method.member span.icon { background-color: #cdf; border: 2px solid #cdf; }
            .sidebar div.Property.member span.icon { background-color: #fcf; border: 2px solid #fcf; }
            .sidebar div.Event.member span.icon { background-color: #faa; border: 2px solid #faa; }
            .sidebar div.Field.member span.icon { background-color: #ee8; border: 2px solid #ee8; }
            .sidebar div.member.missing span.icon { border-color: red; }

            .sidebar div.Class.type span.typeicon { background-color: #4df; border: 2px solid #4df; }
            .sidebar div.Struct.type span.typeicon { background-color: #f9f; border: 2px solid #f9f; }
            .sidebar div.Enum.type span.typeicon { background-color: #4f8; border: 2px solid #4f8; }
            .sidebar div.Interface.type span.typeicon { background-color: #f44; border: 2px solid #f44; }
            .sidebar div.Delegate.type span.typeicon { background-color: #ff4; border: 2px solid #ff4; }
            .sidebar div.type.missing span.typeicon { border-color: red; }

            .sidebar div.legend, .sidebar div.tree { background: #f8f8f8; border: 1px solid black; -moz-border-radius: 5px; padding: .5em; margin-bottom: .7em; }
            .sidebar div.legend p { text-align: center; font-weight: bold; margin: 0 0 0.4em 0; padding: 0.2em 0; background: #ddd; }
            .sidebar ul.tree { margin: .5em 0; padding: 0 0 0 2em; }
            ul { padding-left: 1.5em; margin-bottom: 1em; }
            li { margin-top: 0.7em; margin-bottom: 0.7em; }
            li li { margin: 0; }
            table { border-collapse: collapse; }
            table.layout { border: hidden; width: 100%; margin: 0; }
            table.layout td { vertical-align: top; padding: 0; }
            table.layout td.content { width: 100%; padding: 1em 1em 0em 1.5em; }
            table.doclist td { border: 1px solid #ccc; padding: 1em 2em; background: #eee; }
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
        public DocumentationGenerator(string[] paths) : this(paths, null) { }

        /// <summary>
        /// Initialises a <see cref="DocumentationGenerator"/> instance by searching the given path for XML and DLL files.
        /// All pairs of matching <c>*.dll</c> and <c>*.docs.xml</c> files are considered for documentation. The classes are extracted
        /// from the DLLs and grouped by namespaces.
        /// </summary>
        /// <param name="paths">Paths containing DLL and XML files.</param>
        /// <param name="copyDllFilesTo">Path to copy DLL files to prior to loading them into memory. If null, original DLLs are loaded.</param>
        public DocumentationGenerator(string[] paths, string copyDllFilesTo)
        {
            _namespaces = new SortedDictionary<string, namespaceInfo>();
            _types = new SortedDictionary<string, typeInfo>();
            _members = new SortedDictionary<string, memberInfo>();

            foreach (var path in paths)
            {
                foreach (var f in new DirectoryInfo(path).GetFiles("*.dll").Where(f => File.Exists(f.FullName.Remove(f.FullName.Length - 3) + "docs.xml")))
                {
                    var docsFile = f.FullName.Remove(f.FullName.Length - 3) + "docs.xml";
                    Assembly a;
                    if (copyDllFilesTo != null)
                    {
                        var newFullPath = Path.Combine(copyDllFilesTo, f.Name);
                        File.Copy(f.FullName, newFullPath);
                        a = Assembly.LoadFile(newFullPath);
                    }
                    else
                        a = Assembly.LoadFile(f.FullName);
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
        public HttpRequestHandler GetRequestHandler()
        {
            return req =>
            {
                if (req.RestUrl == "")
                    return HttpServer.RedirectResponse(req.BaseUrl + "/");
                if (req.RestUrl == "/css")
                    return HttpServer.StringResponse(_css, "text/css; charset=utf-8");
                else
                {
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
                                member != null && member.MemberType == MemberTypes.Constructor ? (object) friendlyTypeName(type, false) :
                                    member != null ? member.Name : type != null ? (object) friendlyTypeName(type, false) : ns != null ? ns : null,
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
                                                nkvp.Value.Types.Where(tkvp => !tkvp.Value.Type.IsNested).Select(tkvp => generateTypeBullet(tkvp.Key, type, req))
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
                }
            };
        }

        private IEnumerable<object> friendlyTypeName(Type t, bool includeNamespaces)
        {
            return friendlyTypeName(t, includeNamespaces, null, false);
        }
        private IEnumerable<object> friendlyTypeName(Type t, bool includeNamespaces, string baseURL, bool inclRef)
        {
            if (t.IsByRef)
            {
                if (inclRef)
                    yield return "ref ";
                t = t.GetElementType();
            }

            if (t.IsArray)
            {
                yield return friendlyTypeName(t.GetElementType(), includeNamespaces);
                yield return "[]";
                yield break;
            }

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                yield return friendlyTypeName(t.GetGenericArguments()[0], includeNamespaces);
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
                if (includeNamespaces && !t.IsGenericParameter)
                {
                    yield return new SPAN(t.Namespace) { class_ = "namespace" };
                    yield return ".";
                    var outerTypes = new List<object>();
                    Type outT = t;
                    while (outT.IsNested)
                    {
                        outerTypes.Insert(0, ".");
                        outerTypes.Insert(0, friendlyTypeName(outT.DeclaringType, false, baseURL, false));
                        outT = outT.DeclaringType;
                    }
                    yield return outerTypes;
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
                        yield return friendlyTypeName(ga, includeNamespaces, baseURL, inclRef);
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
                return friendlyTypeName((Type) m, namespaces, baseUrl, false);

            if (m.MemberType == MemberTypes.Constructor || m.MemberType == MemberTypes.Method)
                return new SPAN { class_ = m.MemberType.ToString() }._(
                    friendlyMethodName(m, returnType, containingType, parameterTypes, parameterNames, namespaces, indent, url, baseUrl, false)
                );

            return new SPAN { class_ = m.MemberType.ToString() }._(
                returnType && m.MemberType == MemberTypes.Property ? new object[] { friendlyTypeName(((PropertyInfo) m).PropertyType, namespaces, baseUrl, false), " " } :
                returnType && m.MemberType == MemberTypes.Event ? new object[] { friendlyTypeName(((EventInfo) m).EventHandlerType, namespaces, baseUrl, false), " " } :
                returnType && m.MemberType == MemberTypes.Field ? new object[] { friendlyTypeName(((FieldInfo) m).FieldType, namespaces, baseUrl, false), " " } : null,
                containingType ? friendlyTypeName(m.DeclaringType, namespaces, baseUrl, false) : null,
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
                    yield return friendlyTypeName(meth.ReturnType, namespaces, baseUrl, false);
                    yield return " ";
                }
            }
            if ((m.MemberType == MemberTypes.Constructor || isDelegate) && url != null)
                yield return new STRONG(new A(friendlyTypeName(mi.DeclaringType, namespaces)) { href = url });
            else if (isDelegate)
                yield return new STRONG(friendlyTypeName(mi.DeclaringType, namespaces, baseUrl, false));
            else if (containingType)
                yield return friendlyTypeName(mi.DeclaringType, namespaces, baseUrl, false);
            else if (m.MemberType == MemberTypes.Constructor)
                yield return new STRONG(friendlyTypeName(mi.DeclaringType, namespaces, baseUrl, false));
            if (!indent && (mi.IsGenericMethod || (parameterNames && mi.GetParameters().Any()))) yield return new WBR();
            if (m.MemberType != MemberTypes.Constructor && !isDelegate)
            {
                if (containingType) yield return ".";
                yield return new STRONG(url == null ? (object) cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, false)) : new A(cSharpCompatibleMethodName(m.Name, friendlyTypeName(((MethodInfo) m).ReturnType, false))) { href = url });
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
                       parameterTypes ? friendlyTypeName(p.ParameterType, namespaces, baseUrl, !p.IsOut) : null,
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
                        parameterTypes ? friendlyTypeName(p.ParameterType, namespaces, baseUrl, !p.IsOut) : null,
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

        private object generateTypeBullet(string typeFullName, Type selectedType, HttpRequest req)
        {
            var typeinfo = _types[typeFullName];
            string cssClass = typeinfo.GetTypeCssClass()+" type";
            if (typeinfo.Documentation == null) cssClass += " missing";
            return new DIV { class_ = cssClass }._(new DIV(new SPAN(typeinfo.GetTypeLetters()) { class_ = "typeicon" }, new A(friendlyTypeName(typeinfo.Type, false)) { href = req.BaseUrl + "/" + typeFullName.UrlEscape() }) { class_ = "line" },
                selectedType == null || !isNestedTypeOf(selectedType, typeinfo.Type) || typeof(Delegate).IsAssignableFrom(typeinfo.Type) ? null :
                typeinfo.Members.Where(mkvp => isPublic(mkvp.Value.Member)).Select(mkvp =>
                {
                    string css = mkvp.Value.Member.MemberType.ToString() + " member";
                    if (mkvp.Value.Documentation == null) css += " missing";
                    return mkvp.Value.Member.MemberType != MemberTypes.NestedType
                        ? new DIV { class_ = css }._(new SPAN(mkvp.Value.Member.MemberType.ToString()[0]) { class_ = "icon" }, new A(friendlyMemberName(mkvp.Value.Member, false, false, true, false, false)) { href = req.BaseUrl + "/" + mkvp.Key.UrlEscape() })
                        : generateTypeBullet(GetTypeFullName((Type) mkvp.Value.Member), selectedType, req);
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

            foreach (var gr in _namespaces[namespaceName].Types.Where(t => !t.Value.Type.IsNested).GroupBy(kvp => kvp.Value.Type.IsEnum).OrderBy(gr => gr.Key))
            {
                yield return new H2(gr.Key ? "Enums in this namespace" : "Classes and structs in this namespace");
                yield return new TABLE { class_ = "doclist" }._(
                    gr.Select(kvp => new TR(
                        new TD(new A(friendlyTypeName(kvp.Value.Type, false)) { href = req.BaseUrl + "/" + GetTypeFullName(kvp.Value.Type).UrlEscape() }),
                        new TD(kvp.Value.Documentation == null || kvp.Value.Documentation.Element("summary") == null
                            ? (object) new EM("This type is not documented.")
                            : interpretBlock(kvp.Value.Documentation.Element("summary").Nodes(), req))
                    ))
                );
            }
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
            yield return new UL(new LI("Declared in: ", friendlyTypeName(member.DeclaringType, true, req.BaseUrl, false)));
            yield return new H2("Declaration");
            yield return new PRE((isStatic ? "static " : null), friendlyMemberName(member, true, false, true, true, false, true, null, req.BaseUrl));

            if (document != null)
            {
                var summary = document.Element("summary");
                if (summary != null)
                {
                    yield return new H2("Summary");
                    yield return interpretBlock(summary.Nodes(), req);
                }
                var remarks = document.Element("remarks");
                if (remarks != null)
                {
                    yield return new H2("Remarks");
                    yield return interpretBlock(remarks.Nodes(), req);
                }
                foreach (var example in document.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretBlock(example.Nodes(), req);
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
                                friendlyTypeName(pi.ParameterType, false, req.BaseUrl, !pi.IsOut),
                                " ",
                                new STRONG(pi.Name)
                            ),
                            new TD(docElem == null
                                ? (object) new EM("This parameter is not documented.")
                                : interpretBlock(docElem.Nodes(), req))
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
                friendlyTypeName(type, true)
            );

            LI typeTree = null;
            if (!type.IsAbstract || !type.IsSealed || type.IsInterface)
            {
                var typeRecurse = type;
                while (typeRecurse != null)
                {
                    var ftn = friendlyTypeName(typeRecurse, true, typeRecurse == type ? null : req.BaseUrl, false);
                    typeTree = new LI(typeRecurse == typeof(object) ? "Inheritance:" : typeRecurse == type ? (object) new STRONG(ftn) : ftn, typeRecurse.IsSealed ? " (sealed)" : null, typeTree == null ? null : new UL(typeTree));
                    typeRecurse = typeRecurse.BaseType;
                }
            }

            object derived = null;
            var derivedTypes = _types
                .Select(kvp => kvp.Value.Type)
                .Where(t => t.BaseType == type || (t.BaseType != null && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() == type))
                .ToArray();
            if (derivedTypes.Any())
                derived = new LI("Derived types:", new UL(derivedTypes.Select(t => new LI(friendlyTypeName(t, true, req.BaseUrl, false)))));

            yield return new UL { class_ = "typeinfo" }._(
                new LI("Namespace: ", new A(type.Namespace) { class_ = "namespace", href = req.BaseUrl + "/" + type.Namespace.UrlEscape() }),
                type.IsNested ? new LI("Declared in: ", friendlyTypeName(type.DeclaringType, true, req.BaseUrl, false)) : null,
                typeTree,
                derived
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
                    yield return interpretBlock(summary.Nodes(), req);
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
                                    friendlyTypeName(pi.ParameterType, false, req.BaseUrl, !pi.IsOut),
                                    " ",
                                    new STRONG(pi.Name)
                                ),
                                new TD(docElem == null
                                    ? (object) new EM("This parameter is not documented.")
                                    : interpretBlock(docElem.Nodes(), req))
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
                    yield return interpretBlock(remarks.Nodes(), req);
                }
                foreach (var example in document.Elements("example"))
                {
                    yield return new H2("Example");
                    yield return interpretBlock(example.Nodes(), req);
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
                                : interpretBlock(kvp.Value.Documentation.Element("summary").Nodes(), req))
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
                            docElem == null ? (object) new EM("This type parameter is not documented.") : interpretBlock(docElem.Nodes(), req),
                            constraints == null || constraints.Length == 0 ? null :
                            constraints.Length > 0 ? new object[] { new BR(), new EM("Must be derived from:"), ' ', friendlyTypeName(constraints[0], true, req.BaseUrl, false), 
                                constraints.Skip(1).Select(c => new object[] { ", ", friendlyTypeName(constraints[0], true, req.BaseUrl, false) }) } : null
                        )
                    );
                })
            );
        }

        private IEnumerable<object> interpretBlock(IEnumerable<XNode> nodes, HttpRequest req)
        {
            using (var en = nodes.GetEnumerator())
            {
                if (!en.MoveNext()) yield break;
                if (en.Current is XText)
                {
                    yield return new P(interpretInline(new XElement("para", nodes), req));
                    yield break;
                }

                foreach (var node in nodes)
                {
                    var elem = node is XElement ? (XElement) node : new XElement("para", node);

                    if (elem.Name == "para")
                        yield return new P(interpretInline(elem, req));
                    else if (elem.Name == "list" && elem.Attribute("type") != null && elem.Attribute("type").Value == "bullet")
                        yield return new UL(new Func<IEnumerable<object>>(() =>
                        {
                            return elem.Elements("item").Select(e =>
                                e.Elements("term").Any()
                                    ? (object) new LI(new STRONG(interpretInline(e.Element("term"), req)),
                                        e.Elements("description").Any() ? new BLOCKQUOTE(interpretInline(e.Element("description"), req)) : null)
                                    : e.Elements("description").Any()
                                        ? (object) new LI(e.Element("description").FirstNode is XText ? interpretInline(e.Element("description"), req) : interpretBlock(e.Element("description").Nodes(), req))
                                        : null);
                        }));
                    else if (elem.Name == "code")
                        yield return new PRE(interpretPre(elem, req));
                    else
                        yield return "Unknown element name: " + elem.Name;
                }
            }
        }

        private IEnumerable<object> interpretPre(XElement elem, HttpRequest req)
        {
            // Hideosly complex code to remove common indentation from each line, while allowing something like <see cref="..." /> inside a <code> element.
            // Example: suppose the input is "<code>\n    XYZ<see/>\n\n    ABC</code>". Note <see/> is an inline element.

            // Step 1: Turn all the text nodes into strings, split them at the newlines, then add the newlines back in; turn all the non-text nodes into HTML
            // Example is now: { "", E.N, "    XYZ", [code element], E.N, E.N, "    ABC" }       (E.N stands for Environment.NewLine)
            var everything = elem.Nodes().SelectMany(nod => nod is XText ? Regex.Split(((XText) nod).Value, @"\r\n|\r|\n").SelectMany(lin => new string[] { Environment.NewLine, lin.TrimEnd() }).Skip(1).Cast<object>() : interpretInlineNode((XElement) nod, req));

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

        private IEnumerable<object> interpretInline(XElement elem, HttpRequest req)
        {
            foreach (var node in elem.Nodes())
                yield return interpretInlineNode(node, req);
        }

        private IEnumerable<object> interpretInlineNode(XNode node, HttpRequest req)
        {
            if (node is XText)
                yield return ((XText) node).Value;
            else
            {
                var inElem = node as XElement;
                if (inElem.Name == "see" && inElem.Attribute("cref") != null)
                {
                    Type actual;
                    string token = inElem.Attribute("cref").Value;
                    if (_types.ContainsKey(token))
                        yield return new A(friendlyTypeName(_types[token].Type, false)) { href = req.BaseUrl + "/" + token.UrlEscape() };
                    else if (_members.ContainsKey(token))
                        yield return new A(friendlyMemberName(_members[token].Member, false, false, true, false, false)) { href = req.BaseUrl + "/" + token.UrlEscape() };
                    else if (token.StartsWith("T:") && (actual = Type.GetType(token.Substring(2), false, true)) != null)
                    {
                        yield return actual.Namespace;
                        yield return ".";
                        yield return new STRONG(Regex.Replace(actual.Name, "`\\d+", string.Empty));
                        if (actual.IsGenericType)
                        {
                            yield return "<";
                            yield return actual.GetGenericArguments().First().Name;
                            foreach (var gen in actual.GetGenericArguments().Skip(1))
                            {
                                yield return ", ";
                                yield return gen.Name;
                            }
                            yield return ">";
                        }
                    }
                    else
                        yield return token.Substring(2);
                }
                else if (inElem.Name == "c")
                    yield return new CODE(interpretInline(inElem, req));
                else if (inElem.Name == "paramref" && inElem.Attribute("name") != null)
                    yield return new SPAN(new STRONG(inElem.Attribute("name").Value)) { class_ = "parameter" };
                else
                    yield return interpretInline(inElem, req);
            }
        }
    }
}
