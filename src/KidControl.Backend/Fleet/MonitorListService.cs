using KidControl.Backend.Entities;
using KidControl.Backend.Persistence;
using KidControl.Fleet.Contracts;
using Microsoft.EntityFrameworkCore;

namespace KidControl.Backend.Fleet;

/// <summary>
/// Owns the content-monitor lists (RFC-05): profanity, adult keywords, adult domains and
/// exceptions. Stored in the DB, versioned via <see cref="MonitorMeta"/>. Seeded once from
/// files on the VM (kept out of the public repo) and editable via a one-off admin import;
/// agents fetch the compiled lists from <c>/agent/monitor-lists</c> when their version is behind.
/// </summary>
public sealed class MonitorListService(FleetDbContext db, TimeProvider clock)
{
    public async Task<int> GetVersionAsync(CancellationToken ct = default)
        => (await db.MonitorMetas.AsNoTracking().FirstOrDefaultAsync(ct))?.ListsVersion ?? 0;

    public async Task<MonitorListsDto> GetListsAsync(CancellationToken ct = default)
    {
        var terms = await db.MonitorTerms.AsNoTracking().ToListAsync(ct);
        List<string> Of(string kind) => terms.Where(t => t.Kind == kind).Select(t => t.Value).ToList();
        return new MonitorListsDto
        {
            Version = await GetVersionAsync(ct),
            Profanity = Of(MonitorTermKind.Profanity),
            AdultKeywords = Of(MonitorTermKind.AdultKeyword),
            AdultDomains = Of(MonitorTermKind.AdultDomain),
            Exceptions = Of(MonitorTermKind.Exception),
        };
    }

    /// <summary>Replaces ALL lists with the supplied ones and bumps the version. Returns the new version.</summary>
    public async Task<int> ReplaceAllAsync(MonitorListsDto lists, CancellationToken ct = default)
    {
        db.MonitorTerms.RemoveRange(await db.MonitorTerms.ToListAsync(ct)); // InMemory-friendly (no ExecuteDelete)

        void Add(string kind, IReadOnlyList<string> values)
        {
            foreach (var v in values.Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.Ordinal))
            {
                db.MonitorTerms.Add(new MonitorTerm { Kind = kind, Value = v });
            }
        }

        Add(MonitorTermKind.Profanity, lists.Profanity);
        Add(MonitorTermKind.AdultKeyword, lists.AdultKeywords);
        Add(MonitorTermKind.AdultDomain, lists.AdultDomains);
        Add(MonitorTermKind.Exception, lists.Exceptions);

        var version = await BumpVersionAsync(ct);

        // Propagate the new list version to agents through the normal policy delta sync: bump
        // every device's policy version so the next heartbeat carries the new MonitorListsVersion.
        var now = clock.GetUtcNow();
        foreach (var p in await db.DevicePolicies.ToListAsync(ct))
        {
            p.Version += 1;
            p.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return version;
    }

    private async Task<int> BumpVersionAsync(CancellationToken ct)
    {
        var meta = await db.MonitorMetas.FirstOrDefaultAsync(ct);
        if (meta is null)
        {
            meta = new MonitorMeta { Id = 1, ListsVersion = 0 };
            db.MonitorMetas.Add(meta);
        }

        meta.ListsVersion += 1;
        meta.UpdatedAt = clock.GetUtcNow();
        return meta.ListsVersion;
    }

    /// <summary>
    /// One-time seed from a directory on the VM (files: profanity.txt, adult_keywords.txt,
    /// adult_domains.txt, exceptions.txt — one term per line, '#' comments ignored). No-op if the
    /// dir is missing, the files are empty, or the DB already holds terms.
    /// </summary>
    public async Task SeedFromDirectoryIfEmptyAsync(string? dir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return;
        }
        if (await db.MonitorTerms.AnyAsync(ct))
        {
            return;
        }

        static IReadOnlyList<string> Load(string path) => File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray()
            : [];

        var lists = new MonitorListsDto
        {
            Profanity = Load(Path.Combine(dir, "profanity.txt")),
            AdultKeywords = Load(Path.Combine(dir, "adult_keywords.txt")),
            AdultDomains = Load(Path.Combine(dir, "adult_domains.txt")),
            Exceptions = Load(Path.Combine(dir, "exceptions.txt")),
        };

        if (lists.Profanity.Count + lists.AdultKeywords.Count + lists.AdultDomains.Count == 0)
        {
            return; // nothing usable found
        }

        await ReplaceAllAsync(lists, ct);
    }
}
