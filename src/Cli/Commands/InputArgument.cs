using System.CommandLine;

namespace Anonymizer.Commands;

internal sealed class InputArgument : Argument<FileSystemInfo[]>
{
    public InputArgument() : base("InputFilesOrDir")
    {
        Arity = ArgumentArity.OneOrMore;
        Description = "PDF source files or directory";
        this.AcceptExistingOnly();
    }
}
