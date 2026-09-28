using Microsoft.EntityFrameworkCore;
using RelayPulse.Api.Data;

namespace RelayPulse.Api.Accounts;

/// <summary>Account picker entry. No auth yet (PLAN.md §7): every account is listed.</summary>
public sealed record AccountSummary(int Id, string Name, string Timezone);

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccounts(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/accounts", GetAccountsAsync);
        return app;
    }

    private static async Task<List<AccountSummary>> GetAccountsAsync(RelayDbContext db, CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking()
            .OrderBy(a => a.Id)
            .Select(a => new AccountSummary(a.Id, a.Name, a.Timezone))
            .ToListAsync(cancellationToken);
}
