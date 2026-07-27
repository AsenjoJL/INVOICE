using HazelInvoice.Configuration;
using HazelInvoice.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Services.Clients;

public sealed class ClientGroupService : IClientGroupService
{
    private readonly ApplicationDbContext _context;
    private readonly OperationsOptions _operations;

    public ClientGroupService(
        ApplicationDbContext context,
        IOptions<OperationsOptions> operations)
    {
        _context = context;
        _operations = operations.Value;
    }

    public async Task<IReadOnlyList<string>> GetClientGroupNamesAsync(CancellationToken cancellationToken = default)
    {
        var configuredGroups = _operations.GetClientGroups();
        var databaseGroups = await _context.ClientGroups
            .AsNoTracking()
            .Where(g => g.IsActive)
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.Name)
            .Select(g => g.Name)
            .ToListAsync(cancellationToken);

        var sourceGroups = databaseGroups.Count > 0 ? databaseGroups : configuredGroups;

        return sourceGroups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetOutletGroupNamesAsync(CancellationToken cancellationToken = default)
    {
        var clientGroups = await _context.ClientGroups
            .AsNoTracking()
            .Where(g => g.IsActive)
            .Select(g => new { g.Name, g.OutletGroupNames })
            .ToListAsync(cancellationToken);

        var sourceGroups = clientGroups.Count > 0
            ? clientGroups
                .SelectMany(g => ParseOutletGroupNames(g.OutletGroupNames).DefaultIfEmpty(g.Name))
            : _operations.OutletGroups;

        return sourceGroups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();
    }

    public async Task<string> ResolveClientGroupOrDefaultAsync(string? clientGroup, CancellationToken cancellationToken = default)
    {
        var groups = await GetClientGroupNamesAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(clientGroup))
            return groups.FirstOrDefault() ?? _operations.DefaultClientGroup;

        var trimmed = clientGroup.Trim();
        var match = groups.FirstOrDefault(g => string.Equals(g, trimmed, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match) ? (groups.FirstOrDefault() ?? _operations.DefaultClientGroup) : match;
    }

    public async Task<IReadOnlyList<string>> ResolveOutletGroupsForClientAsync(string? clientGroup, CancellationToken cancellationToken = default)
    {
        var selectedClient = await ResolveClientGroupOrDefaultAsync(clientGroup, cancellationToken);
        var databaseGroup = await _context.ClientGroups
            .AsNoTracking()
            .Where(g => g.IsActive && g.Name.ToLower() == selectedClient.ToLower())
            .Select(g => new { g.Name, g.OutletGroupNames })
            .FirstOrDefaultAsync(cancellationToken);

        if (databaseGroup != null)
        {
            var mapped = ParseOutletGroupNames(databaseGroup.OutletGroupNames)
                .DefaultIfEmpty(databaseGroup.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (mapped.Count > 0)
                return mapped;
        }

        return _operations.ResolveOutletGroupsForClient(selectedClient);
    }

    public static IReadOnlyList<string> ParseOutletGroupNames(string? value)
    {
        return (value ?? string.Empty)
            .Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeOutletGroupNames(IEnumerable<string> names)
    {
        return string.Join(", ", names
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
