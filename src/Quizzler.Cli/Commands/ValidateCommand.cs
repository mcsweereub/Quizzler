using Spectre.Console.Cli;

namespace Quizzler.Cli.Commands;

public sealed class ValidateCommand : AsyncCommand<ContentSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ContentSettings settings, CancellationToken cancellationToken)
    {
        var result = await ContentCheck.RunAsync(settings, cancellationToken);
        return result is { Issues.Count: 0 } ? 0 : 1;
    }
}
