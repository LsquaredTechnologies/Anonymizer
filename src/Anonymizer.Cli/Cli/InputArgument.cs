using System.CommandLine;

namespace Anonymizer.Cli;

internal sealed class InputArgument : Argument<FileSystemInfo[]>
{
    public InputArgument() : base("InputFilesOrDir")
    {
        Arity = ArgumentArity.ZeroOrMore;
        Description = "PDF source files";
        this.AcceptExistingOnly();
    }
}
