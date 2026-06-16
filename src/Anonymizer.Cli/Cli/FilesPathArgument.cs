using System.CommandLine;

namespace Anonymizer.Cli;

internal sealed class FilesPathArgument : Argument<DirectoryInfo>
{
    public FilesPathArgument() : base("FilesDir")
    {
        Arity = ArgumentArity.ExactlyOne;
        Description = "Path to files to automatically anonymize";
        this.AcceptExistingOnly();
    }
}
