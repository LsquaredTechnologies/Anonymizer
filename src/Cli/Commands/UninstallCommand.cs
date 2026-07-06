using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Cli.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Cli.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UninstallCommand : Command
{
    private const string HelpDesc = """
        Uninstalls the application from the user directory.
        """;

    public UninstallCommand(IHostBuilder builder) : base("uninstall", HelpDesc) =>
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var setup = app.Services.GetRequiredService<SetupLifecycleService>();
            setup.Uninstall();
        });
}
