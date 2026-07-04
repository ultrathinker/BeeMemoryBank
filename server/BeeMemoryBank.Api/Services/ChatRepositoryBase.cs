using System.Data;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Base for chat repositories. Resolves connections from the chat-specific
/// <see cref="ChatDbConnectionFactory"/> (distinct from BeeMemoryBank.Storage.DbConnectionFactory),
/// so these repos can never accidentally touch <c>beememorybank.db</c>.
/// </summary>
public abstract class ChatRepositoryBase(ChatDbConnectionFactory factory)
{
    protected readonly ChatDbConnectionFactory Factory = factory;

    protected static string UtcNow() => DateTime.UtcNow.ToString("o");

    protected IDbConnection OpenConnection() => Factory.CreateConnection();
}
