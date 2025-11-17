using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Data;

public class AppContext(DbContextOptions<AppContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure entities.
        
        base.OnModelCreating(modelBuilder);
    }
}