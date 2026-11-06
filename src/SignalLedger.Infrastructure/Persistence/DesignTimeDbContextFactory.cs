using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SignalLedger.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SignalLedgerDbContext>
{
    public SignalLedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SignalLedgerDbContext>()
            .UseSqlite("Data Source=signalledger.design.db")
            .Options;
        return new SignalLedgerDbContext(options);
    }
}
