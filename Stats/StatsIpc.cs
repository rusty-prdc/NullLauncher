using Dapper;

namespace NullLauncher.Stats;

/// <summary>
/// Статистика игры: суммы по сборкам (instances.play_time_ms / launch_count),
/// дневная шкала и последние сессии — всё из launch_history + instances.
/// </summary>
public static class StatsIpc
{
    public static void Register(IpcRouter r, AppServices s)
    {
        r.Register("stats.overview", (_, _) => Task.FromResult<object?>(Overview()));
    }

    private static object Overview()
    {
        // ---- суммарно и по сборкам (из таблицы instances) ----
        var perInstance = Database.DatabaseService.Query<InstRow>("""
            SELECT id AS Id, name AS Name, color AS Color, mc_version AS McVersion, loader AS Loader,
                   play_time_ms AS PlayMs, launch_count AS Launches, last_played_at AS LastPlayedAt
            FROM instances
            ORDER BY play_time_ms DESC
            """);

        long totalMs = perInstance.Sum(x => x.PlayMs);
        int totalLaunches = perInstance.Sum(x => x.Launches);

        // ---- дневная шкала за 14 дней (из launch_history) ----
        var since = DateTime.UtcNow.AddDays(-13).Date;
        var daily = Database.DatabaseService.Query<DailyRow>("""
            SELECT date(started_at) AS Date,
                   SUM(COALESCE(duration_ms,0)) AS Ms,
                   COUNT(*) AS Launches
            FROM launch_history
            WHERE started_at >= @Since
            GROUP BY date(started_at)
            ORDER BY Date
            """, new { Since = since.ToString("o") });

        // ---- последние сессии ----
        var recent = Database.DatabaseService.Query<SessRow>("""
            SELECT h.instance_id AS InstanceId, i.name AS Name, h.started_at AS StartedAt,
                   h.duration_ms AS DurationMs, h.exit_code AS ExitCode
            FROM launch_history h
            LEFT JOIN instances i ON i.id = h.instance_id
            ORDER BY h.started_at DESC
            LIMIT 12
            """);

        return new
        {
            totals = new
            {
                playMs = totalMs,
                launches = totalLaunches,
                instances = perInstance.Count,
                avgSessionMs = totalLaunches > 0 ? totalMs / totalLaunches : 0,
                bestDayMs = daily.Count > 0 ? daily.Max(d => d.Ms) : 0,
            },
            instances = perInstance.Select(x => new
            {
                id = x.Id, name = x.Name, color = x.Color, mcVersion = x.McVersion, loader = x.Loader,
                playMs = x.PlayMs, launches = x.Launches, lastPlayedAt = x.LastPlayedAt,
            }).ToList(),
            daily = daily.Select(d => new { date = d.Date, ms = d.Ms, launches = d.Launches }).ToList(),
            recent = recent.Select(h => new
            {
                instanceId = h.InstanceId, name = h.Name ?? "Удалённая сборка",
                startedAt = h.StartedAt, durationMs = h.DurationMs ?? 0,
                exitCode = h.ExitCode, crashed = h.ExitCode is not null && h.ExitCode != 0,
            }).ToList(),
            since = since.ToString("yyyy-MM-dd"),
        };
    }

    private sealed class InstRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public string? McVersion { get; set; }
        public string? Loader { get; set; }
        public long PlayMs { get; set; }
        public int Launches { get; set; }
        public string? LastPlayedAt { get; set; }
    }

    private sealed class DailyRow
    {
        public string? Date { get; set; }
        public long Ms { get; set; }
        public int Launches { get; set; }
    }

    private sealed class SessRow
    {
        public string? InstanceId { get; set; }
        public string? Name { get; set; }
        public string? StartedAt { get; set; }
        public long? DurationMs { get; set; }
        public int? ExitCode { get; set; }
    }
}
