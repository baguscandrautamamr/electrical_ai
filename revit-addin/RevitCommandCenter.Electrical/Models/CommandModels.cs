using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitCommandCenter.Electrical.Models;

/// <summary>A row from <c>commands_queue</c>.</summary>
public sealed class CommandModel
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("user_id")] public string UserId { get; set; } = string.Empty;
    [JsonProperty("project_id")] public string ProjectId { get; set; } = string.Empty;
    [JsonProperty("command_type")] public string CommandType { get; set; } = string.Empty;
    [JsonProperty("command_text")] public string? CommandText { get; set; }
    [JsonProperty("command_json")] public JObject Params { get; set; } = new();

    /// <summary>
    /// The Telegram chat this command came from, so a generated file can be
    /// sent back to it. Null for a command queued by something other than the
    /// bot.
    /// </summary>
    [JsonProperty("chat_id")] public long? ChatId { get; set; }

    [JsonProperty("status")] public string Status { get; set; } = "pending";
    [JsonProperty("retry_count")] public int RetryCount { get; set; }
    [JsonProperty("max_retries")] public int MaxRetries { get; set; } = 3;
    [JsonProperty("queued_at")] public DateTime QueuedAt { get; set; }

    // --- typed parameter access -------------------------------------------
    // The webhook already validated and normalized these, so a missing value
    // here means a genuine contract break, not user error.

    public string GetString(string name, string fallback = "") =>
        Params[name]?.Type is JTokenType.Null or null ? fallback : Params[name]!.Value<string>() ?? fallback;

    public double GetDouble(string name, double fallback = 0) =>
        Params[name]?.Type is JTokenType.Null or null ? fallback : Params[name]!.Value<double>();

    public int GetInt(string name, int fallback = 0) =>
        Params[name]?.Type is JTokenType.Null or null ? fallback : Params[name]!.Value<int>();

    public bool GetBool(string name, bool fallback = false) =>
        Params[name]?.Type is JTokenType.Null or null ? fallback : Params[name]!.Value<bool>();

    public bool Has(string name) => Params[name]?.Type is not (JTokenType.Null or null);
}

/// <summary>Outcome of executing one command, written back to the queue.</summary>
public sealed class CommandResult
{
    public bool Success { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }
    public string? Stack { get; init; }
    public int ExecutionTimeMs { get; set; }

    /// <summary>
    /// Whether re-running the command could plausibly succeed. A missing family
    /// or an invalid parameter will fail identically every time, so retrying
    /// only delays the user's error message.
    /// </summary>
    public bool Retryable { get; init; } = true;

    public static CommandResult Ok(object data) => new() { Success = true, Data = data };

    public static CommandResult Fail(string error, bool retryable = true, string? stack = null) =>
        new() { Success = false, Error = error, Retryable = retryable, Stack = stack };

    public static CommandResult FromException(Exception ex, bool retryable = true) =>
        new() { Success = false, Error = ex.Message, Stack = ex.ToString(), Retryable = retryable };
}
