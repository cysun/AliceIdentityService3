namespace AliceIdentityService.Models;

// Explicit response contracts: never serialize Identity/OpenIddict entities or arbitrary claims.
public sealed record AdminIdentity(string Id, string? Email, string FirstName, string LastName, string ScreenName);
public sealed record AdminSession(AdminIdentity User, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, ApplicationInfo Application);
public sealed record AdminCounts(long TotalUsers, long ConnectedApplications, long UnconfirmedUsers, long Administrators, long LockedUsers);
public sealed record RecentRegistration(string Id, string Name, string? Email, string Status, DateTime CreatedAt);
public sealed record AdminApplication(string Id, string Name, string? ClientId);
public sealed record AdminOverview(AdminCounts Counts, IReadOnlyList<RecentRegistration> RecentRegistrations,
    IReadOnlyList<AdminApplication> Applications, DateTimeOffset AsOf);
