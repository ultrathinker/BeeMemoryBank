using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace BeeMemoryBank.Platforms.Apple.LaunchAgents;

/// <summary>What a LaunchAgent plist of an app of this product says.</summary>
public sealed record LaunchAgentInfo(string Label, IReadOnlyList<string> ProgramArguments, bool RunAtLoad);

/// <summary>
/// Writes and reads the small property list of an app's per-user LaunchAgent (the blind app and the full app each have their own label).
/// Pure text work, so it is tested on any OS. The agent: its own label, <c>RunAtLoad</c> (start at login), no <c>KeepAlive</c> (Quit means
/// quit), only in a graphical login session (<c>Aqua</c>), and a normal interactive process type (a menu-bar app is not a background
/// daemon to be throttled).
/// </summary>
public static partial class LaunchAgentPlist
{
    /// <summary>A label is a file name too (<c>&lt;label&gt;.plist</c>): letters, digits, dot, dash, underscore; no path characters.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    public static bool IsValidLabel(string? label) => label is not null && LabelPattern().IsMatch(label) && !label.EndsWith('.');

    public static string Build(string label, IReadOnlyList<string> programArguments)
    {
        if (!IsValidLabel(label)) throw new ArgumentException("The label must be letters, digits, '.', '-' and '_' only.", nameof(label));
        if (programArguments is null || programArguments.Count == 0 || string.IsNullOrWhiteSpace(programArguments[0]))
            throw new ArgumentException("A program to start is needed.", nameof(programArguments));
        if (programArguments.Any(a => a is null || a.Any(char.IsControl)))
            throw new ArgumentException("A program argument must not contain control characters.", nameof(programArguments));

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n",
            OmitXmlDeclaration = false,
        };
        using var stream = new MemoryStream();
        using (var xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartDocument();
            xml.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            xml.WriteStartElement("plist");
            xml.WriteAttributeString("version", "1.0");
            xml.WriteStartElement("dict");
            Key(xml, "Label"); xml.WriteElementString("string", label);
            Key(xml, "ProgramArguments");
            xml.WriteStartElement("array");
            foreach (var argument in programArguments) xml.WriteElementString("string", argument);
            xml.WriteEndElement();
            Key(xml, "RunAtLoad"); xml.WriteElementString("true", null);
            Key(xml, "KeepAlive"); xml.WriteElementString("false", null);
            Key(xml, "LimitLoadToSessionType"); xml.WriteElementString("string", "Aqua");
            Key(xml, "ProcessType"); xml.WriteElementString("string", "Interactive");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static void Key(XmlWriter xml, string name) => xml.WriteElementString("key", name);

    /// <summary>Reads a plist; null if it is not a well-formed property list with a label and program arguments.</summary>
    public static LaunchAgentInfo? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var document = System.Xml.Linq.XDocument.Load(reader);
            var dict = document.Root is { Name.LocalName: "plist" } root ? root.Element("dict") : null;
            if (dict is null) return null;

            string? label = null;
            List<string>? arguments = null;
            var runAtLoad = false;
            var children = dict.Elements().ToList();
            for (var i = 0; i + 1 < children.Count; i += 2)
            {
                if (children[i].Name.LocalName != "key") return null;
                var value = children[i + 1];
                switch (children[i].Value)
                {
                    case "Label" when value.Name.LocalName == "string": label = value.Value; break;
                    case "ProgramArguments" when value.Name.LocalName == "array":
                        arguments = value.Elements("string").Select(e => e.Value).ToList(); break;
                    case "RunAtLoad": runAtLoad = value.Name.LocalName == "true"; break;
                }
            }
            return label is null || arguments is null || arguments.Count == 0 ? null : new LaunchAgentInfo(label, arguments, runAtLoad);
        }
        catch (XmlException) { return null; }
    }
}
