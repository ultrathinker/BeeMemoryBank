using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IProjectionMatrixRepository
{
    Task<ProjectionMatrixStore?> GetAsync();
    Task SaveAsync(ProjectionMatrixStore matrix);
}
