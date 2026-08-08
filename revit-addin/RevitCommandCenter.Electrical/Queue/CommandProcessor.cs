using RevitCommandCenter.Electrical.Handlers;
using RevitCommandCenter.Electrical.Models;
using RevitCommandCenter.Electrical.Utils;

namespace RevitCommandCenter.Electrical.Queue;

/// <summary>
/// Routes a command to its handler.
///
/// The registry is built once and shared; handlers are stateless apart from
/// per-execution locals, so a single instance each is fine.
/// </summary>
public sealed class CommandProcessor
{
    private readonly Dictionary<string, ICommandHandler> _handlers;

    public CommandProcessor()
    {
        var handlers = new List<ICommandHandler>
        {
            new LightingHandler(),
            new ReceptacleHandler(),
            new CableTrayHandler(),
            new AddHangersHandler(),
            new FireAlarmHandler(),
            new TelephoneHandler(),
            new LANHandler(),
            new SecurityHandler(),
            new CommunicationHandler(),
            new ExportHandler(),
            new QueryHandler(),
        };

        _handlers = handlers.ToDictionary(
            handler => handler.CommandType,
            StringComparer.OrdinalIgnoreCase);

        // equip_room orchestrates the others, so it is registered last with a
        // read-only view of the registry built so far.
        var orchestrated = new EquipRoomHandler(_handlers);
        _handlers[orchestrated.CommandType] = orchestrated;
    }

    public IReadOnlyDictionary<string, ICommandHandler> Handlers => _handlers;

    public ICommandHandler? Resolve(string commandType) =>
        _handlers.TryGetValue(commandType, out var handler) ? handler : null;

    /// <summary>
    /// Executes a command. Must be called on Revit's main thread.
    ///
    /// Never throws: an unhandled exception here would escape into Revit's
    /// external-event pump and take the add-in down, so everything becomes a
    /// failed <see cref="CommandResult"/> instead.
    /// </summary>
    public CommandResult Execute(HandlerContext context, CommandModel command)
    {
        var handler = Resolve(command.CommandType);
        if (handler is null)
        {
            return CommandResult.Fail(
                $"No handler registered for command type '{command.CommandType}'.",
                retryable: false);
        }

        var startedAt = DateTime.UtcNow;

        try
        {
            Logger.Info($"Executing {command.CommandType} ({command.Id})");
            var result = handler.Execute(context, command);
            result.ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;
            return result;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            // Revit API errors are usually about model state, so retrying the
            // same command against the same model will fail the same way.
            Logger.Error($"Revit API error in {command.CommandType}", ex);
            return new CommandResult
            {
                Success = false,
                Error = $"Revit API error: {ex.Message}",
                Stack = ex.ToString(),
                Retryable = false,
                ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Unhandled error in {command.CommandType}", ex);
            return new CommandResult
            {
                Success = false,
                Error = ex.Message,
                Stack = ex.ToString(),
                Retryable = true,
                ExecutionTimeMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds,
            };
        }
    }
}
