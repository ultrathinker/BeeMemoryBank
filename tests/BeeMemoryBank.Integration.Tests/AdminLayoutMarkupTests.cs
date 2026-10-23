using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Fix round 1 of 2.7.2 (what a real browser showed): the pages are server-rendered, so the layout cannot be measured here, but the markup and the rules that keep a
/// merged page from scrolling sideways on a phone can be pinned: no fixed grid of columns, the classes that let a table lose columns, the cell that keeps its icons in one row.
/// </summary>
public sealed class AdminLayoutMarkupTests
{
    private static readonly string Web = Path.Combine(DiSnapshot.RepoRoot(), "server", "BeeMemoryBank.Web");
    private static readonly string AdminDir = Path.Combine(Web, "Pages", "Admin");

    /// <summary>The pages the merge of 2.7.2 touched.</summary>
    private static readonly string[] MergedPages = ["BlindNodes", "ConnectDevice", "HardDelete", "StorageProtection", "OsAutoUnlock", "TrustedNodes"];

    private static string Page(string name) => File.ReadAllText(Path.Combine(AdminDir, name + ".cshtml"));

    private static string Css() => File.ReadAllText(Path.Combine(Web, "wwwroot", "css", "site.css"));

    /// <summary>One rule of the stylesheet: from its selector to its closing brace (a nested block, an at-rule, counts to its own end).</summary>
    private static string RuleOf(string css, string selector)
    {
        var at = css.IndexOf(selector, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, $"site.css has a rule for {selector}");
        var open = css.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[at..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced braces after " + selector);
    }

    [Fact]
    public void NoMergedPage_LaysItsFieldsInAFixedGrid_ThatCannotCollapseOnAPhone()
    {
        // grid-template-columns: 1fr 1fr / 2fr 1fr / repeat(2, 1fr) never collapse; repeat(auto-fit, minmax(...)) does.
        var fixedGrid = new Regex(@"grid-template-columns:\s*(?!repeat\(\s*auto-(?:fit|fill))", RegexOptions.IgnoreCase);
        foreach (var name in MergedPages)
            fixedGrid.IsMatch(Page(name)).Should().BeFalse($"{name} has a fixed column grid in its markup: use the class admin-fields, which wraps");
    }

    [Fact]
    public void TheTrustedNodesTable_LosesColumnsAndStacksByTheWidthOfItsCard_AndKeepsItsIconsInOneRow()
    {
        var page = Page("TrustedNodes");
        page.Should().Contain("<table class=\"version-table admin-stack\">");
        // the node id and the date added are the first to go: their header and their cells carry the class
        Regex.Matches(page, "class=\"hide-lt-1060").Should().HaveCount(4, "Node ID and Added, header and cell each");
        page.Should().Contain("<th class=\"hide-lt-1060\">Node ID</th>").And.Contain("<th class=\"hide-lt-1060\">Added</th>");
        // the buttons of a row are one flex row inside the cell
        page.Should().Contain("<td class=\"col-actions\">").And.Contain("<div class=\"row-actions\">");
        // every cell that is not the name says what it is when the header is gone (data-label, drawn by the stacked layout)
        foreach (var label in new[] { "API address", "Status", "Auto-restore", "Auto-DEK rotation", "Last contact" })
            page.Should().Contain($"data-label=\"{label}\"");
        page.Should().NotContain("col-label", "the label is the data-label of the cell, not a span of its own");

        var css = Css();
        css.Should().Contain(".admin-scope #promote-password-peers label { overflow-wrap: anywhere; }");
        css.Should().Contain(".admin-scope .admin-body .card:has(> table) { container: admin-table / inline-size; }");
        css.Should().Contain(".admin-scope td.col-name { min-width: 9em; overflow-wrap: anywhere; }");
        RuleOf(css, ".admin-scope td.col-actions, .admin-scope th.col-actions {").Should().Contain("white-space: nowrap");
        RuleOf(css, ".admin-scope .row-actions {").Should().Contain("flex-wrap: nowrap").And.Contain("gap: 2px");
        RuleOf(css, "@container admin-table (max-width: 1060px)").Should().Contain(".hide-lt-1060").And.Contain("display: none !important");
        var stacked = RuleOf(css, "@container admin-table (max-width: 860px)");
        stacked.Should().Contain("table.admin-stack thead { display: none; }").And.Contain("td[data-label]::before { content: attr(data-label);");
        stacked.Should().Contain("min-width: 34px; min-height: 34px", "an icon button is a thumb wide in the stacked row").And.Contain("gap: 6px");
    }

    /// <summary>The cells of a stacked table that say what they are: data-label="..." on a td (the header row is hidden below 860px).</summary>
    private static void HasLabels(string page, params string[] labels)
    {
        foreach (var label in labels) page.Should().Contain($"data-label=\"{label}\"", $"the cell '{label}' must say what it is when the header row is gone");
    }

    [Fact]
    public void TheAgentsTable_FollowsItsCard_LikeTrustedNodes_AndNeverHidesItsDeleteButton()
    {
        var page = Page("Agents");
        page.Should().Contain("<table class=\"version-table admin-stack\">");
        page.Should().Contain("<th class=\"col-name\">Name</th>").And.Contain("<td class=\"col-name\">");
        // the key prefix and the date created go first (header and cell each), and are in the title of the name
        Regex.Matches(page, "class=\"hide-lt-1060").Should().HaveCount(4);
        page.Should().Contain("<th class=\"hide-lt-1060\">API Key</th>").And.Contain("<th class=\"hide-lt-1060\">Created</th>").And.Contain("<strong title=\"API key @agent.KeyPrefix, created");
        HasLabels(page, "Description", "Owner", "Auto-unlock", "Last access", "Requests");
        // the button of a row is in the one flex row of the Actions cell, which carries no hiding class
        Regex.Match(page, "<td class=\"col-actions\">.*?</td>", RegexOptions.Singleline).Value.Should().Contain("<div class=\"row-actions\">").And.Contain("label=\"Delete\"");
        page.Should().Contain("<th class=\"col-actions\">Actions</th>");
        Css().Should().Contain(".admin-scope td.col-api, .admin-scope td.col-contact, .admin-scope td.col-desc, .admin-scope td.col-owner { overflow-wrap: anywhere; }");
    }

    [Fact]
    public void TheTwoAiChatTables_StackBelowASmallCard_AndTheirRowsAndEmptyStatesCarryTheLabels()
    {
        var page = Page("AiChat");
        Regex.Matches(page, "<table class=\"version-table admin-stack-sm\">").Should().HaveCount(2, "the keys and the models");
        page.Should().NotContain("<table class=\"version-table\">");
        page.Should().Contain("<tbody id=\"chat-keys-body\">").And.Contain("<tbody id=\"chat-models-body\">");
        // the sentences in place of the rows are cells of their own class (the padding and the centring are in the stylesheet, so a stacked table can undo them)
        Regex.Matches(page, @"<td colspan=""\d"" class=""col-empty text-muted text-sm"">Loading…</td>").Should().HaveCount(2);
        page.Should().Contain("<th class=\"col-center\">Text</th>").And.Contain("<th class=\"col-actions\">Actions</th>");

        // the rows are strings built by admin.js: the harness scenarios of SiteSpaNavigationTests run the real script and look at them; the source says the same
        var script = File.ReadAllText(Path.Combine(Web, "wwwroot", "js", "pages", "admin.js"));
        foreach (var label in new[] { "Prefix", "Priority", "Enabled", "Status", "Last used", "Model ID", "Text", "Vision", "Image gen" })
            script.Should().Contain($"data-label=\"{label}\"");
        script.Should().Contain("class=\"col-actions\"><div class=\"row-actions\">");
        script.Should().NotContain("style=\"text-align:right;\"").And.NotContain("style=\"text-align:center;padding:16px;\"");

        var css = Css();
        css.Should().Contain("@container admin-table (max-width: 560px) {").And.Contain(".admin-scope table.admin-stack-sm td[data-label]::before { content: attr(data-label);");
        css.Should().Contain(".admin-scope td.col-empty { text-align: center; padding: 16px; }").And.Contain(".admin-scope table.admin-stack-sm td.col-empty { padding: 12px 0; }");
        css.Should().Contain(".admin-scope th.col-center, .admin-scope td.col-center { text-align: center; }").And.Contain(".admin-scope table.admin-stack-sm td.col-center { text-align: left; }");
    }

    [Fact]
    public void TheSearchLatencyTable_StacksBelowASmallCard_AndKeepsWhatTheResultCountsGroupSaid()
    {
        var page = Page("SearchDiagnostics");
        page.Should().Contain("<table class=\"version-table admin-stack-sm\">").And.NotContain("<table class=\"version-table\">");
        // the group header and its four columns are still there above 560px, and not inline-styled: the stacked layout hides the header rows whole
        page.Should().Contain("<th colspan=\"4\" class=\"col-center\">Result counts</th>");
        Regex.Matches(page, @"<th class=""col-center"">(0|1–10|11–100|100\+)</th>").Should().HaveCount(4);
        page.Should().NotContain("style=\"text-align:center;\">0").And.NotContain("style=\"text-align:center;\">@BucketVal");
        // a stacked row: the name, then each number with its label, the four counts naming themselves ("0 results" ...) because the group header is gone
        page.Should().Contain("<td class=\"col-name\"><strong>@label</strong></td>");
        HasLabels(page, "Requests", "p50 (ms)", "p95 (ms)", "0 results", "1–10 results", "11–100 results", "100+ results");
        // the table is drawn only once a search was made in the life of the process: before that there is a sentence (which wraps), not a table
        page.Should().Contain("No searches recorded yet in this process's lifetime.");
    }

    [Theory]
    [InlineData("Agents", 1)]
    [InlineData("AiChat", 2)]
    [InlineData("SearchDiagnostics", 1)]
    public void EveryStackedTable_IsTheDirectChildOfItsCard_SoTheCardIsTheSizeContainer(string name, int tables)
    {
        // `.card:has(> table)` is the container (site.css); a wrapper div between the card and the table would turn it off, and the table would answer to the window, not to its card
        var stacked = Regex.Matches(Page(name), @"<div class=""card""[^>]*>\s*(@\*.*?\*@\s*)?<table class=""version-table admin-stack(-sm)?""", RegexOptions.Singleline);
        stacked.Should().HaveCount(tables);
    }

    [Fact]
    public void TheHardDeleteAuditLog_FitsTheDialogOnAPhone_WithoutChangingWhatItShows()
    {
        var page = Page("HardDelete");
        page.Should().Contain("<table class=\"bmb-table audit-table\"");
        // the same four headers, in the same words
        foreach (var header in new[] { "col-date\">Date<", "col-entity\">Entity<", "col-counts\">Counts (A/F/M)<", "col-actor\">Actor / Node<" }) page.Should().Contain(header);
        var script = File.ReadAllText(Path.Combine(Web, "wwwroot", "js", "pages", "hard-delete.js"));
        script.Should().Contain("<td class=\"col-entity\" style=\"max-width:300px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;\" title=", "the desktop keeps the one-line entity");

        var css = Css();
        var phone = css.LastIndexOf("@media (max-width: 719px) {", css.IndexOf(".admin-scope #dlg-audit .audit-table { table-layout: fixed; }", StringComparison.Ordinal), StringComparison.Ordinal);
        phone.Should().BeGreaterThan(0, "the audit table has a block of its own for a phone");
        var rule = RuleOf(css[phone..], "@media (max-width: 719px) {");
        rule.Should().Contain("overflow-wrap: anywhere").And.Contain("padding: 6px 4px").And.Contain("letter-spacing: 0");
        rule.Should().Contain("td.col-entity { max-width: none !important; white-space: normal !important;", "the inline one-line style of the entity cell is undone on a phone");
        Regex.Matches(rule, @"\.col-(date|entity|counts|actor) \{ width: (\d+)%; \}").Select(m => int.Parse(m.Groups[2].Value)).Sum().Should().Be(100, "the four columns share the dialog");
    }

    [Fact]
    public void TheHardDeleteList_LosesItsSecondaryColumns_OnANarrowCard_AndNeverItsButtons()
    {
        var page = Page("HardDelete");
        page.Should().NotContain("<style>", "the rules are in site.css, where a container query can answer to the width of the card");
        page.Should().Contain("<th class=\"sortable col-created hide-lt-620\"").And.Contain("<th class=\"sortable col-size hide-lt-620\"");
        page.Should().NotContain("<th class=\"sortable col-status\" data-sort=\"status\" style=\"width", "the widths are in the stylesheet, so that a narrow card can drop them");
        page.Should().Contain("<th class=\"col-actions\">Actions</th>");
        page.Should().MatchRegex(@"<div class=""card"" id=""table-container"">\s*<table", "the table is the direct child of its card: the card is the size container");

        var script = File.ReadAllText(Path.Combine(Web, "wwwroot", "js", "pages", "hard-delete.js"));
        Regex.Matches(script, "<td class=\"hide-lt-620\"").Should().HaveCount(2, "the cells of Created and Size follow their headers");
        script.Should().Contain("btn-delete-row", "the trash button is a cell of the row that is never hidden");

        var css = Css();
        RuleOf(css, "@container admin-table (max-width: 620px) {").Should().Contain(".hide-lt-620").And.Contain("display: none !important");
        css.Should().Contain(".admin-scope .bmb-table th, .admin-scope .bmb-table td { padding: 8px 6px; }");
        css.Should().Contain(".admin-scope .item-path { font-size: 11px;").And.Contain("overflow-wrap: anywhere");
    }

    [Fact]
    public void TheConnectPage_GivesItsNotesParagraphSpacing_ItsStopButtonAMargin_AndItsJoinCodeABreak()
    {
        var page = Page("ConnectDevice");
        page.Should().Contain("<div class=\"admin-notes text-muted text-sm\"").And.Contain("<p class=\"admin-note text-muted text-sm\">This door is for joining only");
        page.Should().Contain("asp-page-handler=\"Disable\" style=\"margin-top:12px;\"", "the Stop listening button does not touch the text above it");
        Regex.Match(page, "<code style=\"[^\"]*\">@ep.Code</code>").Value.Should().Contain("overflow-wrap:anywhere").And.NotContain("word-break");
        Css().Should().Contain(".admin-scope .admin-notes p, .admin-scope .admin-note { margin: 0 0 10px; }");
    }

    [Fact]
    public void TheInvitationForm_UsesTheWrappingFields_AndTheRuleWraps()
    {
        var page = Page("BlindNodes");
        Regex.Matches(page, "class=\"admin-fields\"").Should().HaveCount(2, "both rows of the invitation form");
        page.Should().Contain("admin-field-wide");

        var rule = RuleOf(Css(), ".admin-scope .admin-fields {");
        rule.Should().Contain("display: flex").And.Contain("flex-wrap: wrap");
        Css().Should().Contain(".admin-scope .admin-fields > * { flex: 1 1 14rem; min-width: 0; }");
    }
}
