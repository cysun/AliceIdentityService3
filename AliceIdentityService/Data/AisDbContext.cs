using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace AliceIdentityService.Data;

public sealed class AisDbContext(DbContextOptions<AisDbContext> options) : IdentityDbContext<User>(options)
{
    // AIS2 uses the original Identity schema; do not introduce newer passkey tables.
    protected override Version SchemaVersion => IdentitySchemaVersions.Version1;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<User>().Ignore(user => user.FullName);
        builder.Entity<User>().Property(user => user.CreationTime).HasDefaultValueSql("CURRENT_TIMESTAMP");
        // The live AIS2 schema differs from the old CreateSchema.sql here.
        builder.Entity<OpenIddictEntityFrameworkCoreAuthorization>().Property(item => item.Type).HasMaxLength(50);
        builder.Entity<OpenIddictEntityFrameworkCoreToken>().Property(item => item.Type).HasMaxLength(150);
    }
}
