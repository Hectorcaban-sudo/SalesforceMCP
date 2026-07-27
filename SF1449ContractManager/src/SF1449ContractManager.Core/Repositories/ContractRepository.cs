using Microsoft.EntityFrameworkCore;
using SF1449ContractManager.Core.Data;
using SF1449ContractManager.Core.Models;

namespace SF1449ContractManager.Core.Repositories;

public class ContractRepository : IContractRepository
{
    private readonly ContractDbContext _db;

    public ContractRepository(ContractDbContext db) => _db = db;

    public async Task<List<Sf1449Contract>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Contracts
            .AsNoTracking()
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToListAsync(ct);

    public async Task<Sf1449Contract?> GetByIdAsync(int id, CancellationToken ct = default) =>
        await _db.Contracts
            .Include(c => c.LineItems.OrderBy(li => li.SortOrder))
            .Include(c => c.Clauses)
            .Include(c => c.FieldExtractions)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<Sf1449Contract> AddAsync(Sf1449Contract contract, CancellationToken ct = default)
    {
        contract.CreatedAtUtc = DateTime.UtcNow;
        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);
        return contract;
    }

    public async Task UpdateAsync(Sf1449Contract contract, CancellationToken ct = default)
    {
        contract.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.Contracts
            .Include(c => c.LineItems)
            .Include(c => c.Clauses)
            .FirstOrDefaultAsync(c => c.Id == contract.Id, ct);

        if (existing is null)
            throw new InvalidOperationException($"Contract {contract.Id} not found.");

        _db.Entry(existing).CurrentValues.SetValues(contract);

        // Replace child collections wholesale - simplest correct approach for a
        // data-entry screen that submits the full form each save.
        _db.LineItems.RemoveRange(existing.LineItems);
        existing.LineItems = contract.LineItems.Select(li => new ContractLineItem
        {
            Sf1449ContractId = contract.Id,
            SortOrder = li.SortOrder,
            ItemNumber = li.ItemNumber,
            Description = li.Description,
            Quantity = li.Quantity,
            Unit = li.Unit,
            UnitPrice = li.UnitPrice,
            Amount = li.Amount,
            FrequencyOfService = li.FrequencyOfService,
            PerformanceLocation = li.PerformanceLocation
        }).ToList();

        _db.Clauses.RemoveRange(existing.Clauses);
        existing.Clauses = contract.Clauses.Select(cl => new ContractClause
        {
            Sf1449ContractId = contract.Id,
            ClauseNumber = cl.ClauseNumber,
            Title = cl.Title,
            EffectiveDate = cl.EffectiveDate,
            Category = cl.Category,
            IncorporationType = cl.IncorporationType,
            Section = cl.Section,
            IsChecked = cl.IsChecked,
            FullText = cl.FullText
        }).ToList();

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await _db.Contracts.FindAsync([id], ct);
        if (existing is not null)
        {
            _db.Contracts.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task MarkFieldReviewedAsync(int fieldExtractionId, bool reviewed, CancellationToken ct = default)
    {
        var field = await _db.FieldExtractions.FindAsync([fieldExtractionId], ct);
        if (field is not null)
        {
            field.ReviewedByUser = reviewed;
            await _db.SaveChangesAsync(ct);
        }
    }
}
