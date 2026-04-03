using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class SmtpCommandRegistry
{
    private readonly Dictionary<string, ISmtpCommand> _commands;

    public SmtpCommandRegistry(IEnumerable<ISmtpCommand> commands, ILogger<SmtpCommandRegistry> logger)
    {
        _commands = new Dictionary<string, ISmtpCommand>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in commands)
        {
            foreach (var verb in command.SupportedVerbs)
            {
                _commands[verb] = command;
            }
        }

        logger.LogInformation("Registered {CommandCount} SMTP command verbs", _commands.Count);
    }

    public ISmtpCommand Resolve(string verb, UnknownCommand unknownCommand)
    {
        return _commands.TryGetValue(verb, out var command) ? command : unknownCommand;
    }
}
