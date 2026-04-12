using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Search by metadata (title, tags) and optionally by decrypted article body.
/// Body search requires an unlocked session.
/// </summary>
public class SearchService(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    IFolderRepository folderRepo,
    SessionService session)
{
    public async Task<SearchResults> SearchAsync(string query)
    {
        var foldersTask = folderRepo.SearchAsync(query);
        var articlesTask = articleRepo.SearchAsync(query);
        await Task.WhenAll(foldersTask, articlesTask);
        return new SearchResults(await foldersTask, await articlesTask);
    }

    /// <summary>
    /// Searches article bodies by decrypting each one and checking for the query string.
    /// Requires an unlocked session. Results are merged with title/tag matches.
    /// </summary>
    public async Task<SearchResults> SearchWithContentAsync(string query)
    {
        var foldersTask = folderRepo.SearchAsync(query);
        var metadataTask = articleRepo.SearchAsync(query);
        await Task.WhenAll(foldersTask, metadataTask);

        var folderResults = await foldersTask;
        var metadataResults = await metadataTask;

        if (!session.IsUnlocked)
            return new SearchResults(folderResults, metadataResults);

        var matchedIds = new HashSet<Guid>(metadataResults.Select(a => a.Id));
        var bodyMatchIds = new List<Guid>();
        // ...

        var totalCount = await bodyRepo.GetActiveCountAsync();
        var masterDek = session.GetMasterDek();
        try
        {
            const int batchSize = 50;
            for (int offset = 0; offset < totalCount; offset += batchSize)
            {
                var batch = await bodyRepo.GetActiveBatchAsync(batchSize, offset);
                foreach (var body in batch)
                {
                    if (matchedIds.Contains(body.ArticleId))
                        continue;

                    try
                    {
                        var articleDek = DekManager.UnwrapDek(body.EncryptedDek, body.DekIV, masterDek);
                        var plaintext = ArticleEncryptor.Decrypt(body.Ciphertext, body.IV, articleDek);
                        Array.Clear(articleDek);

                        if (plaintext.Contains(query, StringComparison.OrdinalIgnoreCase))
                            bodyMatchIds.Add(body.ArticleId);
                    }
                    catch // AUDIT NOTE: Intentional — a corrupt or incompatible encrypted body
                    {    // (e.g., re-encrypted with a different DEK after key rotation) must not
                    }    // break search for all other articles. Skip and continue.
                }
            }
        }
        finally
        {
            Array.Clear(masterDek);
        }

        if (bodyMatchIds.Count > 0)
        {
            var bodyArticles = await articleRepo.GetByIdsAsync(bodyMatchIds);
            metadataResults.AddRange(bodyArticles);
        }

        return new SearchResults(folderResults, metadataResults);
    }
}
