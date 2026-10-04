namespace BeeMemoryBank.Core.Models;

public record SearchResults(
    List<Folder> Folders,
    List<Article> Articles);
