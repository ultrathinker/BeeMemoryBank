using System.Data;

namespace BeeMemoryBank.Core.Interfaces;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
