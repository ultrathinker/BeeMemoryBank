using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BeeMemoryBank.MacFullPackage.Tests;

/// <summary>
/// The layout of "Bee Memory Bank.app" as rules over a file listing (paths relative to <c>Contents/MacOS</c>, '/' separated), so the
/// same rules run on a real staged app and on the in-memory lists of the controls. The packer (scripts/pack-macos-full.sh) checks the
/// same things in shell; this is the second, independent reader. NOTE the direction of the Vault rule: this is the FULL app, it MUST
/// contain BeeMemoryBank.Vault.dll (in bmbd/ and in api/). The blind apps' opposite rule is not touched by anything here.
/// </summary>
internal static class FullAppLayout
{
    /// <summary>The five products: the desktop shell at the root, bmbd, the Api, the web host and the bmb command-line tool.</summary>
    public static readonly string[] Hosts =
    [
        "BeeMemoryBank.Desktop", "bmbd/BeeMemoryBank.Node", "api/BeeMemoryBank.Api", "web/BeeMemoryBank.Web", "cli/bmb",
    ];

    /// <summary>The Vault's home: the folders whose product holds the vault code.</summary>
    public static readonly string[] VaultHomes = ["bmbd", "api"];

    public const string VaultDll = "BeeMemoryBank.Vault.dll";

    /// <summary>The real directories of the two shared frameworks (no dot in the name); <c>shared/Microsoft.NETCore.App</c> and <c>shared/Microsoft.AspNetCore.App</c> are links to them.</summary>
    /// <summary>Where <c>web/wwwroot</c> points when the static web files live in Contents/Resources (the link text, relative to web/).</summary>
    public const string WwwrootLinkTarget = "../../Resources/web-wwwroot";

    public const string NetCoreFolder = "fx-netcore";
    public const string AspNetCoreFolder = "fx-aspnetcore";

    /// <summary>The files below <paramref name="root"/> as '/'-separated relative paths. Symbolic links are neither listed nor followed (the listing is of real files), hidden files are included.</summary>
    public static List<string> Files(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        return Directory.EnumerateFiles(root, "*", options).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
    }

    /// <summary>One entry of the directory tree: relative path, directory or not, symbolic link or not.</summary>
    public readonly record struct Entry(string Path, bool IsDirectory, bool IsLink);

    /// <summary>The directories (and links) below <paramref name="root"/>; links are listed but not entered.</summary>
    public static List<Entry> Tree(string root)
    {
        var result = new List<Entry>();
        void Walk(string dir)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var info = new FileInfo(path);
                var isLink = info.LinkTarget is not null;
                var isDir = Directory.Exists(path);
                result.Add(new Entry(System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), isDir, isLink));
                if (isDir && !isLink) Walk(path);
            }
        }
        Walk(root);
        return result;
    }

    /// <summary>The tree rules a file listing cannot show: no real directory with a dot in its name (codesign), and the dotted names of the runtime are links.</summary>
    public static IReadOnlyList<string> TreeViolations(IEnumerable<Entry> tree)
    {
        var entries = tree.ToList();
        var bad = new List<string>();
        foreach (var e in entries.Where(e => e.IsDirectory && !e.IsLink && System.IO.Path.GetFileName(e.Path).Contains('.', StringComparison.Ordinal)))
            bad.Add($"{e.Path} is a real directory with a dot in its name: codesign takes it for a bundle and refuses to seal the app");
        foreach (var (link, target) in new[] { ("dotnet/shared/Microsoft.NETCore.App", NetCoreFolder), ("dotnet/shared/Microsoft.AspNetCore.App", AspNetCoreFolder) })
            if (!entries.Any(e => e.Path == link && e.IsLink)) bad.Add($"{link} must be a symbolic link to {target}");
        if (!entries.Any(e => e.IsLink && e.Path.StartsWith("dotnet/host/fxr/", StringComparison.Ordinal))) bad.Add("dotnet/host/fxr/<version> must be a symbolic link");
        return bad;
    }

    public static readonly string[] NativeLibraries =
    [
        "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "api/libsqlite3mc.dylib", "api/libonnxruntime.dylib", "api/libSkiaSharp.dylib",
    ];

    /// <summary>File names of a data folder; none of them may be inside the bundle (the data lives under ~/Library/Application Support/BeeMemoryBankData).</summary>
    private static readonly string[] DataFileNames = [".runtime.json", "node.status.json", "profiles.json", "desktop-settings.json"];

    /// <summary>A folder called "data" at the top of the bundle or of a component (data/..., api/data/...).</summary>
    private static bool IsInDataFolder(string file)
    {
        var segments = file.Split('/');
        return segments[0] == "data" || (segments.Length > 2 && segments[1] == "data");
    }

    /// <summary>The message of the Vault rule, also used by the controls to recognise it.</summary>
    public static string VaultMessage(string home) => $"{home}/{VaultDll} is missing: the full app must contain the Vault";

    /// <summary>
    /// The rules over a listing of real files (paths relative to <c>Contents/MacOS</c>). <paramref name="wwwrootLinked"/>: <c>web/wwwroot</c> is a
    /// symbolic link to <c>Contents/Resources/web-wwwroot</c> (the packer's default), so the static files are not in this listing.
    /// </summary>
    public static IReadOnlyList<string> Violations(IEnumerable<string> listing, bool wwwrootLinked = false)
    {
        var files = listing.Select(f => f.Replace('\\', '/').TrimStart('/')).ToList();
        var set = new HashSet<string>(files, StringComparer.Ordinal);
        var bad = new List<string>();
        if (files.Count == 0) { bad.Add("the package holds no file"); return bad; }

        foreach (var host in Hosts)
        {
            if (!set.Contains(host)) bad.Add($"missing host: {host}");
            foreach (var suffix in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
                if (!set.Contains(host + suffix)) bad.Add($"missing: {host}{suffix}");
        }

        foreach (var home in VaultHomes)
            if (!set.Contains($"{home}/{VaultDll}")) bad.Add(VaultMessage(home));

        foreach (var native in NativeLibraries)
            if (!set.Contains(native)) bad.Add($"missing native library: {native}");

        if (!wwwrootLinked && !files.Any(f => f.StartsWith("web/wwwroot/", StringComparison.Ordinal))) bad.Add("web/wwwroot is missing or empty");

        // the shared runtime: one copy, in dotnet/, and no component carries its own. The real files are in directories WITHOUT dots
        // (host/fxr/v10-0-8, shared/fx-netcore/v10-0-8, shared/fx-aspnetcore/v10-0-8); the dotted names the host needs are symbolic
        // links, which a file listing does not contain (DirectoryViolations checks them).
        if (!set.Contains("dotnet/dotnet")) bad.Add("missing: dotnet/dotnet (the shared runtime's muxer)");
        if (!files.Any(f => f.StartsWith("dotnet/host/fxr/", StringComparison.Ordinal) && f.EndsWith("/libhostfxr.dylib", StringComparison.Ordinal)))
            bad.Add("missing: dotnet/host/fxr/<version>/libhostfxr.dylib");
        var coreclr = files.Where(f => f.EndsWith("/libcoreclr.dylib", StringComparison.Ordinal) || f == "libcoreclr.dylib").ToList();
        if (coreclr.Count != 1) bad.Add($"expected exactly one libcoreclr.dylib (the shared runtime), found {coreclr.Count}");
        else
        {
            var parts = coreclr[0].Split('/');
            if (parts.Length != 5 || parts[0] != "dotnet" || parts[1] != "shared" || parts[2] != NetCoreFolder)
                bad.Add($"libcoreclr.dylib is not in the shared runtime folder dotnet/shared/{NetCoreFolder}/<version>/: {coreclr[0]}");
            else
            {
                var v = parts[3];
                if (!set.Contains($"dotnet/shared/{NetCoreFolder}/{v}/System.Private.CoreLib.dll")) bad.Add("missing: the shared runtime's System.Private.CoreLib.dll");
                if (!set.Contains($"dotnet/shared/{AspNetCoreFolder}/{v}/Microsoft.AspNetCore.dll")) bad.Add($"missing: the ASP.NET Core runtime {v} (it must match NETCore.App)");
            }
        }

        // codesign takes every directory under Contents/MacOS whose name has a dot for a nested bundle and refuses to seal the app
        var dotted = files.Where(f => f.Split('/').SkipLast(1).Any(seg => seg.Contains('.', StringComparison.Ordinal))).ToList();
        if (dotted.Count > 0) bad.Add($"{dotted.Count} file(s) are in a real directory with a dot in its name, which codesign takes for a bundle: {string.Join(", ", dotted.Take(3))}");

        var pdb = files.Count(f => f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        if (pdb > 0) bad.Add($"the package holds {pdb} .pdb file(s)");

        var blind = files.Where(f => Path.GetFileName(f).StartsWith("BeeMemoryBank.BlindDesktop", StringComparison.Ordinal)).ToList();
        if (blind.Count > 0) bad.Add($"the package holds files of the blind desktop app: {string.Join(", ", blind.Take(3))}");

        var data = files.Where(f => DataFileNames.Contains(Path.GetFileName(f), StringComparer.Ordinal)
                                    || f.EndsWith(".db", StringComparison.Ordinal) || f.EndsWith(".db-wal", StringComparison.Ordinal) || f.EndsWith(".vault.lease", StringComparison.Ordinal)
                                    || f.Split('/').Any(seg => seg == "BeeMemoryBankData")
                                    || IsInDataFolder(f)).ToList();
        if (data.Count > 0) bad.Add($"the package holds data-folder files (the data folder must be outside the bundle): {string.Join(", ", data.Take(3))}");

        // the embedding model is optional, and only ever beside the Api
        var models = files.Where(f => Path.GetFileName(f) == "model.onnx").ToList();
        if (models.Any(m => m != "api/model.onnx")) bad.Add($"model.onnx is somewhere else than api/: {string.Join(", ", models.Where(m => m != "api/model.onnx"))}");

        return bad;
    }

    /// <summary>The rules over a real <c>Contents/MacOS</c> folder, plus what a listing cannot show: the executable bits and the Mach-O headers of the programs.</summary>
    public static IReadOnlyList<string> DirectoryViolations(string macosDir)
    {
        var wwwroot = new DirectoryInfo(Path.Combine(macosDir, "web", "wwwroot"));
        var wwwrootLinked = wwwroot.LinkTarget is not null;
        var bad = Violations(Files(macosDir), wwwrootLinked).ToList();
        bad.AddRange(TreeViolations(Tree(macosDir)));
        if (wwwrootLinked)
        {
            if (wwwroot.LinkTarget != WwwrootLinkTarget) bad.Add($"web/wwwroot is a link to '{wwwroot.LinkTarget}', expected '{WwwrootLinkTarget}'");
            var real = Path.GetFullPath(Path.Combine(macosDir, "..", "Resources", "web-wwwroot"));
            if (!Directory.Exists(real) || !Directory.EnumerateFiles(real, "*", SearchOption.AllDirectories).Any()) bad.Add("Contents/Resources/web-wwwroot is missing or empty");
            if (!File.Exists(Path.Combine(macosDir, "web", "wwwroot", "css", "site.css"))) bad.Add("web/wwwroot/css/site.css cannot be read through the link");
        }
        foreach (var program in Hosts.Append("dotnet/dotnet"))
        {
            var path = Path.Combine(macosDir, program.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            using (var s = File.OpenRead(path))
            {
                var info = MachO.Inspect(s);
                if (!info.IsMachO) bad.Add($"{program} is not a Mach-O file");
                else if (!info.HasArm64) bad.Add($"{program} has no arm64 slice");
            }
            if (!OperatingSystem.IsWindows() && !File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute)) bad.Add($"{program} is not executable");
        }
        return bad;
    }

    /// <summary>
    /// The bundle version the packer writes for a VERSION file: its first three numeric components (<c>2.0.3-preview1</c> gives <c>2.0.3</c>),
    /// the same as <c>SHORT_VERSION</c> in scripts/pack-macos-full.sh (all white space removed first, then <c>sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*$/\1/'</c>;
    /// a text that does not start like that stays as it is).
    /// </summary>
    public static string BundleVersion(string versionFileText)
    {
        var v = Regex.Replace(versionFileText, @"\s", "");
        var m = Regex.Match(v, @"^([0-9]+\.[0-9]+\.[0-9]+)");
        return m.Success ? m.Groups[1].Value : v;
    }

    /// <summary>Info.plist rules for the file at <paramref name="plistPath"/>; <paramref name="versionFileText"/> is the content of the VERSION file.</summary>
    public static IReadOnlyList<string> PlistViolations(string plistPath, string versionFileText) =>
        PlistViolations(PlistReader.Read(plistPath), versionFileText);

    /// <summary>The same for the text of a property list (the controls use it, so they need no file).</summary>
    public static IReadOnlyList<string> PlistViolationsFromXml(string xml, string versionFileText) =>
        PlistViolations(PlistReader.ReadXml(xml), versionFileText);

    /// <summary>Info.plist rules; the keys the packer writes and the design asks for.</summary>
    private static IReadOnlyList<string> PlistViolations(Dictionary<string, object?> plist, string versionFileText)
    {
        var bad = new List<string>();
        var expectedVersion = BundleVersion(versionFileText);
        string? Str(string key) => plist.TryGetValue(key, out var v) ? v as string : null;
        bool Flag(string key) => plist.TryGetValue(key, out var v) && v is true;
        void Expect(string key, string value) { if (Str(key) != value) bad.Add($"Info.plist {key} is '{Str(key)}', expected '{value}'"); }

        Expect("CFBundleIdentifier", "com.beememorybank.desktop");
        Expect("CFBundleExecutable", "BeeMemoryBank.Desktop");
        Expect("CFBundleName", "Bee Memory Bank");
        Expect("CFBundleDisplayName", "Bee Memory Bank");
        Expect("CFBundlePackageType", "APPL");
        Expect("LSApplicationCategoryType", "public.app-category.productivity");
        Expect("CFBundleShortVersionString", expectedVersion);
        Expect("CFBundleVersion", expectedVersion);
        if (!Flag("LSMultipleInstancesProhibited")) bad.Add("Info.plist LSMultipleInstancesProhibited is not true");
        if (!Flag("NSHighResolutionCapable")) bad.Add("Info.plist NSHighResolutionCapable is not true");
        // Dock app by default; the menu-bar-only variant sets it to true. A present key must be true, never false.
        if (plist.TryGetValue("LSUIElement", out var ui) && ui is not true) bad.Add("Info.plist LSUIElement is present but not true");
        if (string.IsNullOrWhiteSpace(Str("NSLocalNetworkUsageDescription"))) bad.Add("Info.plist has no NSLocalNetworkUsageDescription");
        if (!(plist.TryGetValue("NSBonjourServices", out var bonjour) && bonjour is List<object?> l && l.Contains("_beememorybank._tcp")))
            bad.Add("Info.plist NSBonjourServices does not hold _beememorybank._tcp");
        if (!(plist.TryGetValue("NSAppTransportSecurity", out var ats) && ats is Dictionary<string, object?> d && d.TryGetValue("NSAllowsLocalNetworking", out var allow) && allow is true))
            bad.Add("Info.plist NSAppTransportSecurity/NSAllowsLocalNetworking is not true");
        if (Str("LSMinimumSystemVersion") is not { } min || !Version.TryParse(min, out var minVersion)) bad.Add("Info.plist LSMinimumSystemVersion is missing or not a version");
        else if (minVersion < new Version(13, 0)) bad.Add($"Info.plist LSMinimumSystemVersion {min} is below 13.0");
        return bad;
    }
}

/// <summary>Reads the little of the XML property-list format that Info.plist uses: strings, booleans, integers, arrays, dictionaries.</summary>
internal static class PlistReader
{
    public static Dictionary<string, object?> Read(string path)
    {
        using var reader = XmlReader.Create(path, Settings);
        return Parse(reader, path);
    }

    public static Dictionary<string, object?> ReadXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), Settings);
        return Parse(reader, "<text>");
    }

    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    private static Dictionary<string, object?> Parse(XmlReader reader, string what)
    {
        var doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict") ?? throw new InvalidOperationException("not a property list: " + what);
        return (Dictionary<string, object?>)Value(dict)!;
    }

    private static object? Value(XElement e) => e.Name.LocalName switch
    {
        "string" => e.Value,
        "true" => true,
        "false" => false,
        "integer" => long.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture),
        "array" => e.Elements().Select(Value).ToList(),
        "dict" => Dict(e),
        _ => e.Value,
    };

    private static Dictionary<string, object?> Dict(XElement e)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var items = e.Elements().ToList();
        for (var i = 0; i + 1 < items.Count; i += 2) result[items[i].Value] = Value(items[i + 1]);
        return result;
    }
}

/// <summary>Reads the first bytes of a file to say whether it is a Mach-O file (thin or fat) and whether it has an arm64 slice. Independent of <c>file(1)</c>.</summary>
internal static class MachO
{
    private const uint CpuTypeArm64 = 0x0100000C;

    public readonly record struct Info(bool IsMachO, bool HasArm64);

    public static Info Inspect(Stream stream)
    {
        Span<byte> head = stackalloc byte[8];
        if (ReadFully(stream, head) < 8) return new Info(false, false);
        var le = BinaryPrimitives.ReadUInt32LittleEndian(head);
        var be = BinaryPrimitives.ReadUInt32BigEndian(head);

        // thin, little-endian (every Apple-silicon and Intel Mach-O): the magic is stored little-endian, cputype follows
        if (le == 0xFEEDFACF) return new Info(true, BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) == CpuTypeArm64);
        if (le == 0xFEEDFACE) return new Info(true, false);

        // fat: big-endian header, then nfat_arch entries (20 bytes, or 32 for the 64-bit variant) that start with the big-endian cputype.
        // 0xCAFEBABE is also the magic of a Java class file, whose next four bytes are a version (>= 45), never a small architecture count.
        if (be == 0xCAFEBABE || be == 0xCAFEBABF)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            if (count is 0 or > 30) return new Info(false, false);
            var entrySize = be == 0xCAFEBABE ? 20 : 32;
            Span<byte> entry = stackalloc byte[4];
            var arm64 = false;
            for (var i = 0; i < count; i++)
            {
                stream.Position = 8 + (long)i * entrySize;
                if (ReadFully(stream, entry) < 4) break;
                if (BinaryPrimitives.ReadUInt32BigEndian(entry) == CpuTypeArm64) arm64 = true;
            }
            return new Info(true, arm64);
        }
        return new Info(false, false);
    }

    private static int ReadFully(Stream s, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = s.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
