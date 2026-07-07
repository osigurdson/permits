using Microsoft.Data.SqlClient;

namespace Permits;

/// <summary>
/// Loads Permits_DW from Permits_OLTP. Idempotent and incremental: transitions
/// are loaded strictly above the highest permit_activity_id already in the
/// fact table, and month-end state snapshots are only written for months past
/// the last snapshotted month. All warehouse writes happen in one transaction,
/// so a failed run loads nothing and the watermarks stay consistent.
/// </summary>
static class EtlRunner
{
    private static readonly PermitState[] TerminalStates =
        [PermitState.Rejected, PermitState.Completed, PermitState.ExpiredTerminal];

    private sealed record NewActivity(
        int ActivityId, int PermitId, string PermitType, ActivityType Type, DateTime Time);

    private sealed record Transition(int PermitId, int TypeKey, int StateKey, DateTime Time);

    public static async Task RunAsync(string password)
    {
        using var oltp = new SqlConnection(DbInit.GetConnStr(DbInit.OltpDbName, password));
        using var dw = new SqlConnection(DbInit.GetConnStr(DbInit.OlapDbName, password));
        await oltp.OpenAsync();
        await dw.OpenAsync();
        using var tx = dw.BeginTransaction();

        var stateKeys = await LoadStateKeysAsync(dw, tx);

        int watermark = await ScalarIntAsync(dw, tx,
            "SELECT COALESCE(MAX(permit_activity_id), 0) FROM fact_permit_transition");
        var activities = await ReadNewActivitiesAsync(oltp, watermark);

        await ExtendDimDateAsync(dw, tx, activities);
        var typeKeys = await LoadTypeKeysAsync(dw, tx, activities);
        int loaded = await LoadTransitionsAsync(dw, tx, stateKeys, typeKeys, activities);
        int snapshotted = await SnapshotCompletedMonthsAsync(dw, tx, stateKeys);

        await tx.CommitAsync();

        int total = await ScalarIntAsync(dw, null,
            "SELECT COUNT(*) FROM fact_permit_transition");
        Console.WriteLine(
            $"Loaded {loaded} new transitions ({total} total), " +
            $"{snapshotted} new state snapshots.");
    }

    // The state a permit is in after an activity; the state it left is simply
    // whatever the previous activity put it in (Initial for the first).
    private static PermitState StateAfter(ActivityType type) => type switch
    {
        ActivityType.Submitted => PermitState.Pending,
        ActivityType.Approved => PermitState.Active,
        ActivityType.Rejected => PermitState.Rejected,
        ActivityType.Renewed => PermitState.Active,
        ActivityType.Suspended => PermitState.Suspended,
        ActivityType.Reinstated => PermitState.Active,
        ActivityType.Expired => PermitState.Expired,
        ActivityType.ExpiredTerminal => PermitState.ExpiredTerminal,
        ActivityType.Completed => PermitState.Completed,
        _ => throw new InvalidOperationException($"Unknown activity type {type}."),
    };

    private static async Task<List<NewActivity>> ReadNewActivitiesAsync(
            SqlConnection oltp, int watermark)
    {
        using var cmd = oltp.CreateCommand();
        cmd.CommandText = """
            SELECT a.activity_id, a.permit_id, p.permit_type,
                   a.activity_type_code, a.activity_time
            FROM permit_activity a
            JOIN permit p ON p.permit_id = a.permit_id
            WHERE a.activity_id > @watermark
            ORDER BY a.activity_id
            """;
        cmd.Parameters.AddWithValue("@watermark", watermark);

        var result = new List<NewActivity>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new NewActivity(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                (ActivityType)reader.GetInt32(3), reader.GetDateTime(4)));
        }
        return result;
    }

    // Adds every day of the new batch's date range that dim_date is missing.
    private static async Task ExtendDimDateAsync(
            SqlConnection dw, SqlTransaction tx, List<NewActivity> activities)
    {
        if (activities.Count == 0)
        {
            return;
        }

        var existing = new HashSet<int>();
        using (var read = dw.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT date_key FROM dim_date";
            using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing.Add(reader.GetInt32(0));
            }
        }

        using var insert = dw.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO dim_date (date_key, [date], month_key) VALUES (@key, @date, @month)";
        var keyParam = insert.Parameters.Add("@key", System.Data.SqlDbType.Int);
        var dateParam = insert.Parameters.Add("@date", System.Data.SqlDbType.Date);
        var monthParam = insert.Parameters.Add("@month", System.Data.SqlDbType.Int);

        var first = activities[0].Time.Date;
        var last = activities[^1].Time.Date;
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            if (existing.Contains(DateKey(day)))
            {
                continue;
            }
            keyParam.Value = DateKey(day);
            dateParam.Value = day;
            monthParam.Value = MonthKey(day);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Dictionary<string, int>> LoadTypeKeysAsync(
            SqlConnection dw, SqlTransaction tx, List<NewActivity> activities)
    {
        var keys = new Dictionary<string, int>();
        using (var read = dw.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT name, permit_type_key FROM dim_permit_type";
            using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        foreach (var type in activities.Select(a => a.PermitType).Distinct())
        {
            if (keys.ContainsKey(type))
            {
                continue;
            }
            using var insert = dw.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO dim_permit_type (name) VALUES (@name);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """;
            insert.Parameters.AddWithValue("@name", type);
            keys[type] = (int)(await insert.ExecuteScalarAsync())!;
        }
        return keys;
    }

    private static async Task<Dictionary<PermitState, int>> LoadStateKeysAsync(
            SqlConnection dw, SqlTransaction tx)
    {
        var keys = new Dictionary<PermitState, int>();
        using var read = dw.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT name, state_key FROM dim_permit_state";
        using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys[Enum.Parse<PermitState>(reader.GetString(0))] = reader.GetInt32(1);
        }
        return keys;
    }

    private static async Task<int> LoadTransitionsAsync(
            SqlConnection dw,
            SqlTransaction tx,
            Dictionary<PermitState, int> stateKeys,
            Dictionary<string, int> typeKeys,
            List<NewActivity> activities)
    {
        using var insert = dw.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO fact_permit_transition
                (permit_activity_id, permit_id, permit_type_key, from_state_key,
                 to_state_key, date_key, transition_time, from_state_entered_time)
            VALUES (@id, @permit, @type, @from, @to, @date, @time, @entered)
            """;
        var p = insert.Parameters;
        p.Add("@id", System.Data.SqlDbType.Int);
        p.Add("@permit", System.Data.SqlDbType.Int);
        p.Add("@type", System.Data.SqlDbType.Int);
        p.Add("@from", System.Data.SqlDbType.Int);
        p.Add("@to", System.Data.SqlDbType.Int);
        p.Add("@date", System.Data.SqlDbType.Int);
        p.Add("@time", System.Data.SqlDbType.DateTime2);
        p.Add("@entered", System.Data.SqlDbType.DateTime2);

        // Where each permit currently is: (state, time it got there). Filled
        // from the warehouse the first time a permit shows up in the batch,
        // then kept up to date as we walk the batch in activity order.
        var lastState = new Dictionary<int, (int StateKey, DateTime? Time)>();

        foreach (var a in activities)
        {
            if (!lastState.TryGetValue(a.PermitId, out var prev))
            {
                prev = await ReadLastStateAsync(dw, tx, stateKeys, a.PermitId);
            }

            p["@id"].Value = a.ActivityId;
            p["@permit"].Value = a.PermitId;
            p["@type"].Value = typeKeys[a.PermitType];
            p["@from"].Value = prev.StateKey;
            p["@to"].Value = stateKeys[StateAfter(a.Type)];
            p["@date"].Value = DateKey(a.Time);
            p["@time"].Value = a.Time;
            p["@entered"].Value = (object?)prev.Time ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync();

            lastState[a.PermitId] = (stateKeys[StateAfter(a.Type)], a.Time);
        }
        return activities.Count;
    }

    private static async Task<(int StateKey, DateTime? Time)> ReadLastStateAsync(
            SqlConnection dw,
            SqlTransaction tx,
            Dictionary<PermitState, int> stateKeys,
            int permitId)
    {
        using var cmd = dw.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT TOP 1 to_state_key, transition_time
            FROM fact_permit_transition
            WHERE permit_id = @permit
            ORDER BY permit_activity_id DESC
            """;
        cmd.Parameters.AddWithValue("@permit", permitId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetInt32(0), reader.GetDateTime(1));
        }
        return (stateKeys[PermitState.Initial], null);
    }

    // Walks all transitions in time order, tracking each permit's current
    // state. Every time the clock crosses a month boundary the tracked states
    // are written as that month's snapshot (non-terminal states only, and only
    // for months newer than what is already snapshotted). A month is complete
    // exactly when a transition at or past its end has been seen.
    private static async Task<int> SnapshotCompletedMonthsAsync(
            SqlConnection dw, SqlTransaction tx, Dictionary<PermitState, int> stateKeys)
    {
        int lastMonth = await ScalarIntAsync(dw, tx,
            "SELECT COALESCE(MAX(month_key), 0) FROM fact_permit_state");

        var transitions = new List<Transition>();
        using (var read = dw.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT permit_id, permit_type_key, to_state_key, transition_time
                FROM fact_permit_transition
                ORDER BY transition_time, permit_activity_id
                """;
            using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                transitions.Add(new Transition(
                    reader.GetInt32(0), reader.GetInt32(1),
                    reader.GetInt32(2), reader.GetDateTime(3)));
            }
        }
        if (transitions.Count == 0)
        {
            return 0;
        }

        using var insert = dw.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO fact_permit_state (month_key, permit_id, permit_type_key, state_key)
            VALUES (@month, @permit, @type, @state)
            """;
        var p = insert.Parameters;
        p.Add("@month", System.Data.SqlDbType.Int);
        p.Add("@permit", System.Data.SqlDbType.Int);
        p.Add("@type", System.Data.SqlDbType.Int);
        p.Add("@state", System.Data.SqlDbType.Int);

        var terminalKeys = TerminalStates.Select(s => stateKeys[s]).ToHashSet();
        var current = new Dictionary<int, (int TypeKey, int StateKey)>();
        var firstTime = transitions[0].Time;
        var monthEnd = new DateTime(firstTime.Year, firstTime.Month, 1).AddMonths(1);
        int written = 0;

        foreach (var t in transitions)
        {
            while (t.Time >= monthEnd)
            {
                var month = monthEnd.AddMonths(-1);
                if (MonthKey(month) > lastMonth)
                {
                    foreach (var (permitId, s) in current)
                    {
                        if (terminalKeys.Contains(s.StateKey))
                        {
                            continue;
                        }
                        p["@month"].Value = MonthKey(month);
                        p["@permit"].Value = permitId;
                        p["@type"].Value = s.TypeKey;
                        p["@state"].Value = s.StateKey;
                        await insert.ExecuteNonQueryAsync();
                        written++;
                    }
                }
                monthEnd = monthEnd.AddMonths(1);
            }
            current[t.PermitId] = (t.TypeKey, t.StateKey);
        }
        return written;
    }

    private static int DateKey(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;

    private static int MonthKey(DateTime d) => d.Year * 100 + d.Month;

    private static async Task<int> ScalarIntAsync(
            SqlConnection conn, SqlTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
}
