using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RT.TagSoup;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace RT.DocGen
{
    enum TokenType
    {
        Name,
        Dot,
        OpenParen,
        CloseParen,
        OpenCurly,
        CloseCurly,
        TypeGenericParameterReference,
        MethodGenericParameterReference,
        At,
        Star,
        Comma,
        Array,
        EndOfFile
    }

    sealed class Token
    {
        public TokenType Type { get; private set; }
        public string Item { get; private set; }
        public Token(TokenType type, string item = null) { Type = type; Item = item; }
        public override string ToString() => $"{Type}{(Item != null ? " " : null)}{Item}";
    }

    enum GenericParameterSource { Type, Method }

    enum Assumption { None, Member, Type }

    abstract class CrefNode
    {
        public MemberInfo GetMember() { return getMember(null, null, 0); }
        protected static MemberInfo getMember(CrefNode node, Type[] typeGenerics, Type[] methodGenerics, int numGenerics) { return node.getMember(typeGenerics, methodGenerics, numGenerics); }
        protected abstract MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics);
        protected static string getName(CrefNode node, int numGenerics) { return node.getName(numGenerics); }
        protected virtual string getName(int numGenerics) { return null; }
        public abstract object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml);
        protected static int getGenericCount(CrefNode node) { return node == null ? 0 : node.getGenericCount(); }
        protected virtual int getGenericCount() { return 0; }
    }
    sealed class CrefName : CrefNode
    {
        public string Name;
        public CrefNode Parent;
        protected override string getName(int numGenerics)
        {
            return Parent == null
                ? Name + (numGenerics > 0 ? "`" + numGenerics : "")
                : getName(Parent, 0).NullOr(name => name + "." + Name + (numGenerics > 0 ? "`" + numGenerics : ""));
        }
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            Type type;
            if (Parent == null || (type = getMember(Parent, typeGenerics, methodGenerics, 0) as Type) == null)
                return getName(numGenerics).NullOr(Type.GetType);
            return type.GetMember(Name).NullOr(candidates => candidates.Length == 1 ? candidates[0] : null);
        }
        protected override int getGenericCount()
        {
            return
                (Name.Contains("``") ? Convert.ToInt32(Name.Substring(Name.IndexOf("``") + 2)) : Name.Contains('`') ? Convert.ToInt32(Name.Substring(Name.IndexOf('`') + 1)) : 0) +
                (Parent == null ? 0 : getGenericCount(Parent));
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            var type = GetMember() as Type;
            if (type != null)
                return typeHtml(type);

            var nameWithoutGen = Name.Contains('`') ? Name.Substring(0, Name.IndexOf('`')) : Name;
            var numGen = Name.Contains("``") ? Convert.ToInt32(Name.Substring(Name.IndexOf("``") + 2)) : Name.Contains('`') ? Convert.ToInt32(Name.Substring(Name.IndexOf('`') + 1)) : 0;
            switch (assumption)
            {
                case Assumption.Member:
                    object mname = new STRONG(nameWithoutGen);
                    if (numGen > 0)
                        mname = new[] { mname, "<", Enumerable.Range(0, numGen).Select(i => "TM" + (i + 1)).JoinString(", "), ">" };
                    return Parent == null ? mname : new object[] { Parent.GetHtml(Assumption.Type, memberHtml, typeHtml), ".", mname };

                case Assumption.Type:
                    object tname = Name;
                    if (numGen > 0)
                        tname = new[] { tname, "<", Enumerable.Range(getGenericCount(Parent), numGen).Select(i => "T" + (i + 1)).JoinString(", "), ">" };
                    var parentHtml = Parent.NullOr(p => p.GetHtml(Assumption.None, memberHtml, typeHtml));
                    return Parent == null || parentHtml == null ? tname : new object[] { parentHtml, ".", tname };

                default:
                    if (Name.Contains('`'))
                        goto case Assumption.Type;
                    return Parent.NullOr(p => p.GetHtml(Assumption.None, memberHtml, typeHtml)).NullOr(html => new object[] { html, ".", Name });
            }
        }
    }
    sealed class CrefGenericParameter : CrefNode
    {
        public GenericParameterSource Source;
        public int Position;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            return (Source == GenericParameterSource.Type ? typeGenerics : methodGenerics).NullOr(arr => arr[Position]);
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            return (Source == GenericParameterSource.Type ? "T" : "TM") + (Position + 1);
        }
    }
    sealed class CrefGenericType : CrefNode
    {
        public CrefNode[] Arguments;
        public CrefNode Parent;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            Ut.Assert(Parent != null);
            var arguments = Arguments.Select(a => getMember(a, typeGenerics, methodGenerics, 0)).ToArray();
            if (arguments.Any(member => member == null || !(member is Type)))
                return null;
            return (getMember(Parent, typeGenerics, methodGenerics, arguments.Length) as Type).NullOr(type => type.MakeGenericType(arguments.Cast<Type>().ToArray()));
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            return new[] { Parent.GetHtml(Assumption.Type, memberHtml, typeHtml), "<", Arguments.Select(a => a.GetHtml(Assumption.Type, memberHtml, typeHtml)).InsertBetween<object>(", "), ">" };
        }
    }
    sealed class CrefArrayType : CrefNode
    {
        public CrefNode Inner;
        public int Ranks;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            Ut.Assert(Inner != null);
            return (getMember(Inner, typeGenerics, methodGenerics, 0) as Type).NullOr(type => type.MakeArrayType(Ranks));
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            return new[] { Inner.GetHtml(Assumption.Type, memberHtml, typeHtml), "[", new string(',', Ranks - 1), "]" };
        }
    }
    sealed class CrefRefType : CrefNode
    {
        public CrefNode Inner;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            Ut.Assert(Inner != null);
            return (getMember(Inner, typeGenerics, methodGenerics, 0) as Type).NullOr(type => type.MakeByRefType());
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            return new[] { "ref ", Inner.GetHtml(Assumption.Type, memberHtml, typeHtml) };
        }
    }
    sealed class CrefPointerType : CrefNode
    {
        public CrefNode Inner;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int numGenerics)
        {
            Ut.Assert(Inner != null);
            return (getMember(Inner, typeGenerics, methodGenerics, 0) as Type).NullOr(type => type.MakePointerType());
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            return new[] { Inner.GetHtml(Assumption.Type, memberHtml, typeHtml), "*" };
        }
    }
    sealed class CrefMethod : CrefNode
    {
        public string Original;
        public CrefNode Member;
        public CrefNode[] ParameterTypes;
        protected override MemberInfo getMember(Type[] typeGenerics, Type[] methodGenerics, int ___)
        {
            var parent = Member as CrefName;
            Ut.Assert(parent != null && parent.Parent != null);
            return (getMember(parent.Parent, typeGenerics, methodGenerics, 0) as Type)
                .NullOr(type =>
                {
                    var typeGen = type.IsGenericTypeDefinition ? type.GetGenericArguments() : new Type[0];
                    return type.GetMethods().FirstOrDefault(m =>
                    {
                        var methodGen = m.IsGenericMethodDefinition ? m.GetGenericArguments() : new Type[0];
                        return m.Name == parent.Name + (m.IsGenericMethodDefinition ? "`" + methodGen.Length : "") &&
                            m.GetParameters().Select(p => p.ParameterType).SequenceEqual(ParameterTypes.Select(p => getMember(p, typeGen, methodGen, 0)).Select(p => p as Type));
                    });
                });
        }
        public override object GetHtml(Assumption assumption, Func<MemberInfo, object> memberHtml, Func<Type, object> typeHtml)
        {
            var member = GetMember();
            return member != null
                ? memberHtml(member)
                : new SPAN { class_ = "Method", title = Original }._(
                    Member.GetHtml(Assumption.Member, memberHtml, typeHtml),
                    "(", ParameterTypes.Select(p => p.GetHtml(Assumption.Type, memberHtml, typeHtml)).InsertBetween<object>(", "), ")"
                );
        }
    }

    static class CrefParser
    {
        private static Regex _lexer = new Regex(@"
                (?<name>(?!\d)\w+(?:``?\d+)?)|   # name, optionally with number of generics
                (?<mg>``\d+)|                             # method generic parameter
                (?<tg>`\d+)|                                # type generic parameter
                (?<array>\[\])|                            # array
                (?<marray>\[0\:(,0\:)*\])|         # multi-dimensional array
                (?<sc>\(|\)|\{|\}|\.|@|\*|,)|          # single-character tokens
                .                                                   # fallback
            ", RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);

        private static IEnumerable<Token> lex(string input)
        {
            return _lexer.Matches(input).Cast<Match>().Select(m =>
            {
                if (m.Groups["name"].Success)
                    return new Token(TokenType.Name, m.Groups["name"].Value);
                if (m.Groups["mg"].Success)
                    return new Token(TokenType.MethodGenericParameterReference, m.Groups["mg"].Value.Substring(2));
                if (m.Groups["tg"].Success)
                    return new Token(TokenType.TypeGenericParameterReference, m.Groups["tg"].Value.Substring(1));
                if (m.Groups["array"].Success)
                    return new Token(TokenType.Array, "1");
                if (m.Groups["marray"].Success)
                    return new Token(TokenType.Array, ((m.Groups["marray"].Value.Length - 1) / 3).ToString());
                if (m.Groups["sc"].Success)
                    switch (m.Groups["sc"].Value[0])
                    {
                        case '(': return new Token(TokenType.OpenParen);
                        case ')': return new Token(TokenType.CloseParen);
                        case '{': return new Token(TokenType.OpenCurly);
                        case '}': return new Token(TokenType.CloseCurly);
                        case '@': return new Token(TokenType.At);
                        case '*': return new Token(TokenType.Star);
                        case ',': return new Token(TokenType.Comma);
                        case '.': return new Token(TokenType.Dot);
                        default:
                            throw new InvalidOperationException("Unexpected character match: {0}".Fmt(m.Groups["sc"].Value[0]));
                    }
                throw new InvalidOperationException("Unexpected character in input: {0}".Fmt(m.Value));
            }).Concat(new Token(TokenType.EndOfFile));
        }

        public static CrefNode Parse(string input)
        {
            int i = 0;
            var tokens = lex(input).ToArray();
            var result = parse(tokens, ref i, input);
            if (tokens[i].Type != TokenType.EndOfFile)
                throw new InvalidOperationException("Extra unparseable stuff at the end.");
            return result;
        }

        private static CrefNode parse(Token[] tokens, ref int i, string originalInput)
        {
            var member = parseMember(tokens, ref i);
            if (tokens[i].Type != TokenType.OpenParen)
                return member;

            // parse list of parameter types
            i++;
            if (tokens[i].Type == TokenType.CloseParen)
            {
                // zero parameters
                i++;
                return new CrefMethod { Member = member, ParameterTypes = new CrefNode[0], Original = originalInput };
            }

            var parameterTypes = new List<CrefNode>();

            while (true)
            {
                parameterTypes.Add(parseType(tokens, ref i));
                switch (tokens[i].Type)
                {
                    case TokenType.CloseParen:
                        i++;
                        return new CrefMethod { Member = member, ParameterTypes = parameterTypes.ToArray(), Original = originalInput };
                    case TokenType.Comma:
                        i++;
                        continue;
                    default:
                        throw new InvalidOperationException("Parse error: ',' or ')' expected.");
                }
            }
        }

        private static CrefNode parseMember(Token[] tokens, ref int i)
        {
            CrefNode node = null;
            while (true)
            {
                if (tokens[i].Type != TokenType.Name)
                    throw new InvalidOperationException("Parse Error: expected identifier.");
                node = new CrefName { Name = tokens[i].Item, Parent = node };
                i++;
                if (tokens[i].Type != TokenType.Dot)
                    break;
                i++;
            }
            return node;
        }

        private static CrefNode parseType(Token[] tokens, ref int i)
        {
            if (tokens[i].Type == TokenType.TypeGenericParameterReference || tokens[i].Type == TokenType.MethodGenericParameterReference)
            {
                var ret = new CrefGenericParameter
                {
                    Position = Convert.ToInt32(tokens[i].Item),
                    Source = tokens[i].Type == TokenType.TypeGenericParameterReference ? GenericParameterSource.Type : GenericParameterSource.Method
                };
                i++;
                return ret;
            }

            CrefNode node = null;
            while (true)
            {
                if (tokens[i].Type != TokenType.Name)
                    throw new InvalidOperationException("Parse Error: expected identifier.");
                node = new CrefName { Name = tokens[i].Item, Parent = node };
                i++;
                if (tokens[i].Type == TokenType.OpenCurly)
                {
                    i++;
                    var arguments = new List<CrefNode> { parseType(tokens, ref i) };
                    while (tokens[i].Type == TokenType.Comma)
                    {
                        i++;
                        arguments.Add(parseType(tokens, ref i));
                    }
                    if (tokens[i].Type != TokenType.CloseCurly)
                        throw new InvalidOperationException("Parse Error: '}' or ',' expected.");
                    i++;
                    node = new CrefGenericType { Arguments = arguments.ToArray(), Parent = node };
                }
                if (tokens[i].Type != TokenType.Dot)
                    break;
                i++;
            }

            while (tokens[i].Type == TokenType.Array)
            {
                node = new CrefArrayType { Inner = node, Ranks = Convert.ToInt32(tokens[i].Item) };
                i++;
            }

            if (tokens[i].Type == TokenType.At)
            {
                node = new CrefRefType { Inner = node };
                i++;
            }
            else if (tokens[i].Type == TokenType.Star)
            {
                node = new CrefPointerType { Inner = node };
                i++;
            }

            return node;
        }
    }
}
