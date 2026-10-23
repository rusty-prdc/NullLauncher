using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dapper;

namespace NullLauncher.Skins;

/// <summary>
/// Гардероб скинов: локальное хранилище PNG-текстур (64×64 / легаси 64×32) с метаданными в SQLite.
/// Файлы лежат в <see cref="Core.AppPaths.SkinsDir"/>, превью отдаётся как data URL — UI не читает файлы вне UI/.
/// Применение скина к лицензионному аккаунту идёт через <see cref="Accounts.AccountService.ChangeSkinAsync"/>.
/// </summary>
public sealed class SkinService
{
    private readonly Core.AppServices _s;

    public sealed record SkinDto(
        string Id, string Name, string Variant, bool Slim, string? Sha1, string Source,
        DateTime CreatedAt, DateTime? LastUsedAt, string? Preview, long SizeBytes, int SortOrder);

    public SkinService(Core.AppServices s) => _s = s;

    /* ------------------------------------------------------------ список */

    public List<SkinDto> List()
    {
        var rows = Database.DatabaseService.Query<SkinRow>(
            "SELECT * FROM skins ORDER BY sort_order, created_at").ToList();
        SeedDefaults();
        if (rows.Count == 0)
            rows = Database.DatabaseService.Query<SkinRow>(
                "SELECT * FROM skins ORDER BY sort_order, created_at").ToList();

        return rows.Select(r => new SkinDto(
            r.Id, r.Name, r.Variant,
            string.Equals(r.Variant, "slim", StringComparison.OrdinalIgnoreCase),
            r.Sha1, r.Source,
            ParseIso(r.CreatedAt), ParseIso(r.LastUsedAt),
            PreviewDataUrl(r.File),
            File.Exists(r.File) ? new FileInfo(r.File).Length : 0,
            r.SortOrder)).ToList();
    }

    /* ------------------------------------------------------------ добавление */

    /// <summary>Импортирует скин из локального PNG. Возвращает созданную запись.</summary>
    public SkinDto AddFromFile(string path, string? name, string? variant, int? sortOrder = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new LauncherException("Файл скина не найден", path ?? "путь не указан");
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Скин должен быть PNG", Path.GetExtension(path));

        ValidateSkinPng(path);
        var slim = DetectSlim(path);
        var resolvedVariant = ResolveVariant(variant, slim);

        var id = Guid.NewGuid().ToString("N")[..16];
        Directory.CreateDirectory(_s.Paths.SkinsDir);
        var target = Path.Combine(_s.Paths.SkinsDir, id + ".png");
        try { File.Copy(path, target, overwrite: true); }
        catch (Exception ex) { throw new LauncherException("Не удалось сохранить скин", ex.Message, ex); }

        var finalName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(path)
            : name.Trim();

        Database.DatabaseService.Execute("""
            INSERT INTO skins (id, name, file, variant, sha1, source, account_uuid, created_at, last_used_at, sort_order)
            VALUES (@Id, @Name, @File, @Variant, @Sha1, 'local', NULL, @CreatedAt, NULL, @SortOrder)
            """, new
        {
            Id = id, Name = finalName, File = target, Variant = resolvedVariant,
            Sha1 = Sha1Of(target), CreatedAt = DateTime.UtcNow.ToString("o"),
            SortOrder = sortOrder ?? NextSortOrder(),
        });

        Log.Info($"Гардероб: добавлен скин '{finalName}' ({resolvedVariant})");
        _s.Emit("skins.changed", new { action = "added", id });
        return Require(id);
    }

    /// <summary>Импортирует скин по ссылке (например textures.minecraft.net/texture/&lt;hash&gt;).</summary>
    public async Task<SkinDto> AddFromUrlAsync(string url, string? name, string? variant, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new LauncherException("Не указана ссылка на скин");

        Directory.CreateDirectory(_s.Paths.TempDir);
        var tmp = Path.Combine(_s.Paths.TempDir, $"skin-{Guid.NewGuid():N}.png");
        try
        {
            using var http = HttpFactory.Get(_s);
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LauncherException("Не удалось скачать скин",
                    $"HTTP {(int)resp.StatusCode} по ссылке {url}");
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs).ConfigureAwait(false);

            return AddFromFile(tmp, name, variant);
        }
        catch (LauncherException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new LauncherException("Не удалось скачать скин", ex.Message, ex); }
        finally { TryDelete(tmp); }
    }

    /* ------------------------------------------------------------ изменение */

    public void Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new LauncherException("Имя скина не может быть пустым");
        var n = Database.DatabaseService.Execute(
            "UPDATE skins SET name=@Name WHERE id=@Id", new { Name = name.Trim(), Id = id });
        if (n == 0) throw new LauncherException("Скин не найден", id);
        _s.Emit("skins.changed", new { action = "renamed", id });
    }

    public void Remove(string id)
    {
        var file = Database.DatabaseService.QueryFirstOrDefault<string>(
            "SELECT file FROM skins WHERE id=@Id", new { Id = id });
        if (file is null) return;
        Database.DatabaseService.Execute("DELETE FROM skins WHERE id=@Id", new { Id = id });
        TryDelete(file);
        Log.Info($"Гардероб: удалён скин {id}");
        _s.Emit("skins.changed", new { action = "removed", id });
    }

    /// <summary>Применяет скин из гардероба к лицензионному (Microsoft) аккаунту.</summary>
    public async Task<Accounts.AccountDto> ApplyAsync(string id, CancellationToken ct)
    {
        var row = Database.DatabaseService.QueryFirstOrDefault<SkinRow>(
            "SELECT * FROM skins WHERE id=@Id", new { Id = id })
            ?? throw new LauncherException("Скин не найден", id);
        if (!File.Exists(row.File))
            throw new LauncherException("Файл скина отсутствует", row.File);

        var acc = await _s.Accounts.ChangeSkinAsync(row.File, row.Variant, ct).ConfigureAwait(false);
        Database.DatabaseService.Execute(
            "UPDATE skins SET last_used_at=@T WHERE id=@Id",
            new { T = DateTime.UtcNow.ToString("o"), Id = id });
        _s.Emit("skins.changed", new { action = "applied", id, uuid = acc.Uuid });
        return acc;
    }

    /// <summary>Возвращает путь к файлу скина (для переименования/открытия).</summary>
    public string FilePath(string id) =>
        Database.DatabaseService.QueryFirstOrDefault<string>(
            "SELECT file FROM skins WHERE id=@Id", new { Id = id })
        ?? throw new LauncherException("Скин не найден", id);

    /* ------------------------------------------------------------ детекция slim/classic */

    /// <summary>
    /// Определяет slim (3px руки) по прозрачному пикселью (54,20): это 4-я колонка оверлея
    /// правой руки, которая есть только у классической модели (4px). Легаси 64×32 всегда classic.
    /// </summary>
    public static bool DetectSlim(string path)
    {
        try
        {
            using var bmp = new Bitmap(path);
            if (bmp.Width != 64 || bmp.Height < 32) return false;
            if (bmp.Height == 32) return false; // легаси-скины всегда широкие

            // канонический признак slim: 4-я колонка оверлея правой руки прозрачна
            return bmp.GetPixel(54, 20).A < 8;
        }
        catch (Exception ex)
        {
            Log.Debug($"Определение модели скина: {ex.Message}");
            return false;
        }
    }

    /* ------------------------------------------------------------ служебное */

    private SkinDto Require(string id) =>
        List().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
        ?? throw new LauncherException("Скин не найден после сохранения", id);

    private static string ResolveVariant(string? variant, bool slimDetected)
    {
        if (string.Equals(variant, "slim", StringComparison.OrdinalIgnoreCase)) return "slim";
        if (string.Equals(variant, "classic", StringComparison.OrdinalIgnoreCase)) return "classic";
        return slimDetected ? "slim" : "classic";
    }

    private static int NextSortOrder() =>
        (Database.DatabaseService.Scalar<int?>("SELECT MAX(sort_order) FROM skins") ?? 0) + 1;

    /// <summary>При первом запуске кладёт в гардероб дефолтные скины Mojang (Steve, Alex).</summary>
    private void SeedDefaults()
    {
        try
        {
            var count = Database.DatabaseService.Scalar<int>("SELECT COUNT(1) FROM skins");
            if (count > 0) return;

            Directory.CreateDirectory(_s.Paths.SkinsDir);
            var seeded = new (string name, string variant, bool slim)[]
            {
                ("Steve", "classic", false), ("Alex", "slim", true),
            };
            int order = 0;
            foreach (var (name, variant, _) in seeded)
            {
                var src = Path.Combine(_s.Paths.UiDir, "assets", "skins", name.ToLowerInvariant() + ".png");
                if (!File.Exists(src)) continue;
                var id = Guid.NewGuid().ToString("N")[..16];
                var target = Path.Combine(_s.Paths.SkinsDir, id + ".png");
                File.Copy(src, target, overwrite: true);
                Database.DatabaseService.Execute("""
                    INSERT INTO skins (id, name, file, variant, sha1, source, account_uuid, created_at, last_used_at, sort_order)
                    VALUES (@Id, @Name, @File, @Variant, @Sha1, 'default', NULL, @CreatedAt, NULL, @SortOrder)
                    """, new
                {
                    Id = id, Name = name, File = target, Variant = variant,
                    Sha1 = Sha1Of(target), CreatedAt = DateTime.UtcNow.ToString("o"),
                    SortOrder = order++,
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Гардероб: не удалось заложить дефолтные скины: {ex.Message}"); }
    }

    private static string? PreviewDataUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length > 2 * 1024 * 1024) return null; // скины обычно 1–4 КБ; потолок для защиты IPC
            return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
        }
        catch { return null; }
    }

    private static string? Sha1Of(string path)
    {
        try { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant(); }
        catch { return null; }
    }

    /// <summary>Проверяет сигнатуру PNG и размер текстуры (64×64 или легаси 64×32).</summary>
    private static void ValidateSkinPng(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length < 8 || fi.Length > 1024 * 1024)
            throw new LauncherException("Неподходящий файл скина", $"Размер {fi.Length} байт; ожидается PNG до 1 МБ");

        var header = new byte[24];
        using (var fs = File.OpenRead(path))
        {
            if (fs.Read(header, 0, 24) < 24) throw new LauncherException("Повреждённый PNG", "файл слишком мал");
        }
        ReadOnlySpan<byte> sig = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        if (!header.AsSpan(0, 8).SequenceEqual(sig))
            throw new LauncherException("Файл не является PNG", Path.GetFileName(path));

        int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        if (w != 64 || (h != 64 && h != 32))
            throw new LauncherException("Неподходящий размер скина",
                $"{w}×{h}; Minecraft принимает текстуру 64×64 или 64×32");
    }

    private static DateTime ParseIso(string? s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : default;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не мешаем основному сценарию */ }
    }

    private sealed class SkinRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public string Variant { get; set; } = "classic";
        public string? Sha1 { get; set; }
        public string Source { get; set; } = "local";
        public string? AccountUuid { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? LastUsedAt { get; set; }
        public int SortOrder { get; set; }
    }
}
