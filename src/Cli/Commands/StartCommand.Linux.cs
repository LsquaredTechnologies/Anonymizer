using System.CommandLine;

using Anonymizer.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Commands;

internal sealed partial class StartCommand : Command
{
    private const string HelpDesc = """
        Starts the application in the background.
        """;

    public StartCommand(IHostBuilder builder) : base("start", HelpDesc)
    {
        Hidden = true;
        SetAction(async (_) => await ExecuteAsync(builder));
    }
}
