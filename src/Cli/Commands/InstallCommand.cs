using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class InstallCommand : Command
{
    private const string HelpDesc = """
        Installs the application into the user directory.
        """;

    private readonly Option<bool> _noAutostartOption = new("--no-autostart")
    {
        Description = "Do not enable autostart after installation.",
        DefaultValueFactory = (_) => false,
    };

    public InstallCommand(IHostBuilder builder) : base("install", HelpDesc)
    {
        Add(_noAutostartOption);
        SetAction((parseResult) =>
        {
            var app = builder.Build();
            var setup = app.Services.GetRequiredService<SetupLifecycleService>();
            var noAutostart = parseResult.GetRequiredValue(_noAutostartOption);
            return setup.InstallAsync(enableAutostart: !noAutostart);
        });
    }
}
