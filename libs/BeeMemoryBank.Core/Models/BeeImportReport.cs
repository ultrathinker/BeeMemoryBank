namespace BeeMemoryBank.Core.Models;

public class BeeImportReport
{
    public string RootFolderPath { get; set; } = "";
    public int FoldersCreated { get; set; }
    public int ArticlesCreated { get; set; }
    public int ImagesImported { get; set; }
    public int ArticlesSkippedProtected { get; set; }
    public List<string> Warnings { get; set; } = [];
}
