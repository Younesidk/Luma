using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Luma.Infrastructure.Database;

/// <summary>Used only by the EF Core tooling (dotnet ef migrations ...).</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LumaDbContext>
{
    public LumaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LumaDbContext>()
            .UseSqlite("Data Source=luma-design.db")
            .Options;
        return new LumaDbContext(options);
    }
}
