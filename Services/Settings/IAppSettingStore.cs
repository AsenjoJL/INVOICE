namespace HazelInvoice.Services.Settings;

public interface IAppSettingStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

