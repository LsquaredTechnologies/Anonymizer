using System.CommandLine;
using System.Runtime.Versioning;

using Anonymizer.Lifetime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Anonymizer.Commands;

[SupportedOSPlatform("Linux")]
[SupportedOSPlatform("Windows")]
internal sealed class UpdateCommand : Command
{
    private const string HelpDesc = """
        Updates the application in the user directory if a newer version is available.
        """;

    public UpdateCommand(IHostBuilder builder) : base("update", HelpDesc) =>
        SetAction((_) =>
        {
            var app = builder.Build();
            var setup = app.Services.GetRequiredService<SetupLifecycleService>();
            return setup.UpdateAsync();
        });
}
