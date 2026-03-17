using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services.Settings;

public class DbAppSettingStore : IAppSettingStore
{
    private readonly ApplicationDbContext _db;

    public DbAppSettingStore(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var row = await _db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        return row?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Setting key is required.", nameof(key));

        var existing = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing == null)
        {
            _db.AppSettings.Add(new AppSetting
            {
                Key = key.Trim(),
                Value = value,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }
}

