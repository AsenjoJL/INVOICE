namespace HazelInvoice.Services.Clients;

public interface IClientGroupService
{
    Task<IReadOnlyList<string>> GetClientGroupNamesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetOutletGroupNamesAsync(CancellationToken cancellationToken = default);

    Task<string> ResolveClientGroupOrDefaultAsync(string? clientGroup, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ResolveOutletGroupsForClientAsync(string? clientGroup, CancellationToken cancellationToken = default);
}
