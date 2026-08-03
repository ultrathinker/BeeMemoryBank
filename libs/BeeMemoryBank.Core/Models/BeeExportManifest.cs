namespace BeeMemoryBank.Core.Models;

/// <summary>
/// Sidecar metadata written as ".bmb-manifest.json" at the root of a folder/all export ZIP.
/// The ZIP itself stays a plain, human-browsable tree of ".md" files + "attachments/" — this
/// manifest exists purely so <c>BeeImportService</c> can restore what a flat file listing can't
/// represent: the original folder's own name, empty folders, exact titles/tags, and which
/// articles were password-protected (and therefore exported as a placeholder notice, not real
/// content).
/// </summary>
public sealed class BeeExportManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAt { get; set; }

    /// <summary>
    /// The exported folder's own name (e.g. "Мой гитхаб"), or null for a root/"export all" export —
    /// there is no single folder identity to preserve in that case. BeeImportService creates a
    /// subfolder named this at the chosen destination; when null, it imports directly into the
    /// chosen destination with no extra wrapping folder.
    /// </summary>
    public string? SourceFolderName { get; set; }

    /// <summary>
    /// Every folder under the export root, as paths RELATIVE to it ("" = the root itself,
    /// "Аудиты/2026-07-19" = a nested subfolder). Includes folders with zero articles — the
    /// only reason this list exists is so empty folders survive the round trip.
    /// </summary>
    public List<string> Folders { get; set; } = [];

    public List<BeeExportManifestArticle> Articles { get; set; } = [];
}

public sealed class BeeExportManifestArticle
{
    /// <summary>Path of the ".md" file inside the ZIP, relative to the export root.</summary>
    public string File { get; set; } = "";

    /// <summary>The article's real title — NOT necessarily recoverable from the sanitized
    /// filename slug (special characters, length limits).</summary>
    public string Title { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>True if this article is password-protected (second-layer encryption). Its ".md"
    /// content is a placeholder notice, not the real body — BeeImportService skips these and
    /// reports them as warnings instead of creating a fake article.</summary>
    public bool Protected { get; set; }
}
