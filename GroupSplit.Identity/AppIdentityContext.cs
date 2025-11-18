using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GroupSplit.Identity;

public class AppIdentityContext(DbContextOptions<AppIdentityContext> options) : IdentityDbContext<User>(options);