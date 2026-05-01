namespace BeeMemoryBank.Api.Helpers;

public static class PathHelper
{
    public static string Display(string? path) =>
        string.IsNullOrEmpty(path) || path == "/" ? "the root folder" : $"'{path}'";
}
