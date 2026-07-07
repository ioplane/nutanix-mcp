using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NutanixMcp.Data.Repositories;

public sealed class UnitOfWork(NutanixDbContext db) : IUnitOfWork
{
    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct) =>
        db.Database.BeginTransactionAsync(ct);

    public void ClearChangeTracker() => db.ChangeTracker.Clear();
}
