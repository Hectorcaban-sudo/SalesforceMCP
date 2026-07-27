using SF1449ContractManager.Core.Models;

namespace SF1449ContractManager.Core.Repositories;

public interface IContractRepository
{
    Task<List<Sf1449Contract>> GetAllAsync(CancellationToken ct = default);
    Task<Sf1449Contract?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Sf1449Contract> AddAsync(Sf1449Contract contract, CancellationToken ct = default);
    Task UpdateAsync(Sf1449Contract contract, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    Task MarkFieldReviewedAsync(int fieldExtractionId, bool reviewed, CancellationToken ct = default);
}
