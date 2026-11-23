using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using System.Xml.Xsl;

namespace KeeDesk.Core.Services;

internal sealed class KeePassXPathContext : XsltContext
{
    public KeePassXPathContext() : base(new NameTable()) => AddNamespace("kp", "urn:keepass");

    public override bool Whitespace => true;
    public override bool PreserveWhitespace(XPathNavigator node) => true;
    public override int CompareDocument(string baseUri, string nextBaseUri) =>
        string.CompareOrdinal(baseUri, nextBaseUri);

    public override IXsltContextVariable ResolveVariable(string prefix, string name) =>
        throw new XPathException("Variables are not supported.");

    public override IXsltContextFunction ResolveFunction(string prefix, string name, XPathResultType[] argTypes) =>
        (prefix, name) switch
        {
            ("kp", "matches") => new StringFunction(3, XPathResultType.Boolean, Match),
            ("kp", "string-compare") => new StringFunction(4, XPathResultType.Number, Compare),
            _ => throw new XPathException("Unknown XPath function.")
        };

    private (string Pattern, string Flags, Regex Matcher)? _lastMatch;

    private object Match(string[] args)
    {
        var pattern = args[1];
        var flags = args.Length > 2 ? args[2] : string.Empty;
        if (_lastMatch is not { } cached || cached.Pattern != pattern || cached.Flags != flags)
        {
            var options = RegexOptions.CultureInvariant;
            foreach (var flag in flags)
            {
                options |= flag switch
                {
                    's' => RegexOptions.Singleline,
                    'm' => RegexOptions.Multiline,
                    'i' => RegexOptions.IgnoreCase,
                    'x' => RegexOptions.IgnorePatternWhitespace,
                    'q' => RegexOptions.None,
                    _ => throw new XPathException("Unknown regular expression flag.")
                };
            }
            var regex = new Regex(flags.Contains('q') ? Regex.Escape(pattern) : pattern, options,
                TimeSpan.FromSeconds(2));
            cached = (pattern, flags, regex);
            _lastMatch = cached;
        }
        return cached.Matcher.IsMatch(args[0]);
    }

    private static object Compare(string[] args)
    {
        var left = args[0];
        var right = args[1];
        var language = args.Length > 2 ? args[2] : string.Empty;
        var options = args.Length > 3 ? args[3] : string.Empty;
        var culture = language.Length == 0 ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(language);
        if (options.Any(option => option is not ('i' or 'u')))
            throw new XPathException("Unknown string comparison option.");

        var comparison = culture.CompareInfo;
        var insensitive = comparison.Compare(left, right, CompareOptions.IgnoreCase);
        if (options.Contains('i') || insensitive != 0)
            return (double)insensitive;

        var sensitive = comparison.Compare(left, right);
        return (double)(options.Contains('u') ? -Math.Sign(sensitive) : sensitive);
    }

    private static string ToXPathString(object value)
    {
        if (value is XPathNodeIterator nodes)
        {
            var iterator = nodes.Clone();
            return iterator.MoveNext() ? iterator.Current?.Value ?? string.Empty : string.Empty;
        }
        return value switch
        {
            bool boolean => boolean ? "true" : "false",
            XPathNavigator navigator => navigator.Value,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private sealed class StringFunction(int maxArgs, XPathResultType returnType, Func<string[], object> invoke)
        : IXsltContextFunction
    {
        public XPathResultType[] ArgTypes { get; } = Enumerable.Repeat(XPathResultType.String, maxArgs).ToArray();
        public int Minargs => 2;
        public int Maxargs => maxArgs;
        public XPathResultType ReturnType => returnType;

        public object Invoke(XsltContext xsltContext, object[] args, XPathNavigator docContext) =>
            invoke(Array.ConvertAll(args, ToXPathString));
    }
}
