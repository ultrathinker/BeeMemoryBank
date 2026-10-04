namespace BeeMemoryBank.Core.Models;

public class ObsidianImportReport
{
    public string RootFolderPath { get; set; } = "";
    public int ArticlesCreated { get; set; }
    public int ImagesImported { get; set; }
    public int FilesSkipped { get; set; }
    public List<string> Warnings { get; set; } = [];
}
