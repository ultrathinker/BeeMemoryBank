using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-338: the mark of a folder with rules of its own is the ordinary folder icon with an orange diamond inside it. The page script draws it
/// as an inline SVG; these tests pin the two shapes and the colour variable at the source, so the folder cannot drift away from the plain
/// icon (the contour must stay the very path of Bootstrap's folder2, which the plain icon draws) and the diamond cannot lose its colour.
/// The look in the browser (same box, same colour as the plain icon, every theme) is checked by the browser run, not here.
/// </summary>
public class FolderMarkDrawingTests
{
    private const string Diamond = "M8 7.5 10.5 10 8 12.5 5.5 10z";

    private static readonly string Web = Path.Combine(FindRepoRoot(), "server", "BeeMemoryBank.Web", "wwwroot");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the repo root from " + AppContext.BaseDirectory);
    }

    private static string MarksScript() => File.ReadAllText(Path.Combine(Web, "js", "site-folder-marks.js"));

    private static string SiteCss() => File.ReadAllText(Path.Combine(Web, "css", "site.css"));

    [Fact]
    public void TheContourOfTheMark_IsExactlyThePathOfTheVendoredFolder2Icon()
    {
        var vendored = File.ReadAllText(Path.Combine(Web, "lib", "shoelace", "assets", "icons", "folder2.svg"));
        var path = Regex.Match(vendored, "<path d=\"([^\"]+)\"").Groups[1].Value;
        path.Should().StartWith("M1 3.5A1.5 1.5 0 0 1 2.5 2h2.764", "the premise: this is the outline the plain icon draws");

        MarksScript().Should().Contain($"var CONTOUR = '{path}';", "the mark's contour is the unchanged folder2 path, holes included, so it looks identical to the plain icon");
    }

    [Fact]
    public void TheDiamond_IsDrawnInsideTheFolderBox_AndPaintedFromOneVariable()
    {
        MarksScript().Should().Contain($"var DIAMOND = '{Diamond}';");
        MarksScript().Should().Contain("setAttribute('viewBox', '0 0 16 16')", "the same 16x16 box as the plain icon");

        // The contour takes no colour of its own (it is painted with currentColor, like the plain icon); the diamond has the variable.
        var css = SiteCss();
        css.Should().Contain(".tree-folder-mark-diamond { fill: var(--folder-mark-diamond); }");
        css.Should().NotContain("tree-folder-mark-contour", "a colour of its own on the contour would make it differ from the plain icon");
    }

    [Fact]
    public void TheDiamondColour_IsOneVariable_OrangeOnDarkSurfaces_AndDeeperOrangeOnLight()
    {
        var css = SiteCss();
        var root = Regex.Match(css, @"\[data-theme=""ocean""\], :root \{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;
        var light = Regex.Match(css, @"\[data-theme=""light""\] \{(.*?)\n\}", RegexOptions.Singleline).Groups[1].Value;

        root.Should().Contain("--folder-mark-diamond: #ff9500;");
        light.Should().Contain("--folder-mark-diamond: #e06c00;");
    }

    [Fact]
    public void TheMark_IsBuiltInTheScript_NoPictureFileIsFetched()
    {
        // An inline SVG needs no file, so no picture, no static-file rule for it and no connect-src question.
        MarksScript().Should().Contain("createElementNS(SVG_NS, 'svg')").And.NotContain("folder2-filled").And.NotContain(".svg'");
        File.Exists(Path.Combine(Web, "images", "folder2-filled.svg")).Should().BeFalse();
    }
}
