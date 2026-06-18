using System.CommandLine;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

internal sealed class RunCommand : Command
{
    private const string HelpDesc = """
        The "run" command runs anonymizer on a file or a whole directory.
        """;

    private readonly InputArgument _input = new();

    public RunCommand(IHostBuilder builder) : base("run", HelpDesc)
    {
        Add(_input);
        SetAction(async (result, cancellationToken) =>
        {
            var app = builder.Build();
            var anonymizer = app.Services.GetRequiredService<AnonymizerService>();
            var inputFiles = result.GetRequiredValue(_input);
            await anonymizer.AnonymizeFilesAsync(inputFiles, cancellationToken);
        });
    }
}
