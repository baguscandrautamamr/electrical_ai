using Newtonsoft.Json;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Database;

/// <summary>
/// Queue operations.
///
/// Claiming goes through the <c>claim_next_command</c> RPC rather than a
/// SELECT-then-UPDATE: two Revit instances polling the same project would
/// otherwise both see the same pending row and execute the command twice. The
/// RPC does the claim in one statement with <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
public sealed class CommandQueueRepository
{
    private readonly SupabaseClient _client;
    private readonly string _projectId;
    private readonly string _workerId;
    private readonly int _timeoutSeconds;

    public CommandQueueRepository(
        SupabaseClient client,
        string projectId,
        int timeoutSeconds)
    {
        _client = client;
        _projectId = projectId;
        _timeoutSeconds = timeoutSeconds;
        // Identifies this Revit instance in claimed_by, so a stuck command can
        // be traced back to the machine that took it.
        _workerId = $"{Environment.MachineName}/{Environment.ProcessId}";
    }

    public string WorkerId => _workerId;

    /// <summary>Claims the oldest pending command, or null when idle.</summary>
    public async Task<CommandModel?> ClaimNextAsync(CancellationToken ct = default)
    {
        var rows = await _client.RpcAsync<List<CommandModel>>(
            "claim_next_command",
            new
            {
                p_project_id = _projectId,
                p_worker_id = _workerId,
                p_timeout_seconds = _timeoutSeconds,
            },
            ct).ConfigureAwait(false);

        return rows is { Count: > 0 } ? rows[0] : null;
    }

    public async Task CompleteAsync(
        string commandId,
        object resultData,
        int executionTimeMs,
        CancellationToken ct = default)
    {
        await _client.RpcAsync(
            "complete_command",
            new
            {
                p_command_id = commandId,
                p_result = resultData,
                p_execution_time_ms = executionTimeMs,
            },
            ct).ConfigureAwait(false);

        Logger.Info($"Command {commandId} completed in {executionTimeMs} ms");
    }

    public async Task FailAsync(
        string commandId,
        string error,
        string? stack,
        bool retryable,
        CancellationToken ct = default)
    {
        await _client.RpcAsync(
            "fail_command",
            new
            {
                p_command_id = commandId,
                p_error = Truncate(error, 2000),
                p_stack = Truncate(stack, 6000),
                p_retryable = retryable,
            },
            ct).ConfigureAwait(false);

        Logger.Warn($"Command {commandId} failed (retryable={retryable}): {error}");
    }

    /// <summary>Writes a command's outcome back, whichever way it went.</summary>
    public async Task ReportAsync(
        CommandModel command,
        CommandResult result,
        CancellationToken ct = default)
    {
        if (result.Success && result.Data is not null)
        {
            await CompleteAsync(command.Id, result.Data, result.ExecutionTimeMs, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await FailAsync(
                command.Id,
                result.Error ?? "Unknown error",
                result.Stack,
                result.Retryable,
                ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------
    // Device persistence — mirrors what was written into the Revit model so
    // the schedules and the Telegram replies agree with the model.
    // ------------------------------------------------------------------

    public async Task UpsertDevicesAsync(
        string table,
        IEnumerable<object> rows,
        CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        await _client.UpsertAsync(table, list, "project_id,device_id", ct).ConfigureAwait(false);
        Logger.Debug($"Upserted {list.Count} rows into {table}");
    }

    public async Task<string> UpsertCableTrayAsync(object row, CancellationToken ct = default)
    {
        await _client.UpsertAsync("cable_trays", new[] { row }, "project_id,tray_id", ct)
            .ConfigureAwait(false);

        var trayId = JsonConvert.DeserializeObject<Dictionary<string, object>>(
            JsonConvert.SerializeObject(row))?["tray_id"]?.ToString() ?? string.Empty;

        var found = await _client.SelectAsync<Dictionary<string, object>>(
            "cable_trays",
            $"select=id&project_id=eq.{_projectId}&tray_id=eq.{Uri.EscapeDataString(trayId)}&limit=1",
            ct).ConfigureAwait(false);

        return found.Count > 0 ? found[0]["id"]?.ToString() ?? string.Empty : string.Empty;
    }

    public async Task UpsertHangersAsync(IEnumerable<object> rows, CancellationToken ct = default)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;

        await _client.UpsertAsync("cable_tray_hangers", list, "project_id,hanger_id", ct)
            .ConfigureAwait(false);
        Logger.Debug($"Upserted {list.Count} hangers");
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
