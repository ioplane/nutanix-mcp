using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NutanixMcp.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NutanixDbContext>
{
    public NutanixDbContext CreateDbContext(string[] args)
    {
        string cs = Environment.GetEnvironmentVariable("NUTANIX_MCP_CS")
            ?? "Host=localhost;Port=5435;Database=nutanix;Username=nutanix;Password=nutanix";
        var ds = DataSourceFactory.Build(cs);
        var opt = new DbContextOptionsBuilder<NutanixDbContext>()
            .UseNpgsql(ds, o => o.UseVector())
            .Options;
        return new NutanixDbContext(opt);
    }
}
