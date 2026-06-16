using System.CommandLine;

namespace Anonymizer.Cli;

internal sealed class ModelPathArgument : Argument<DirectoryInfo>
{
    public ModelPathArgument() : base("ModelsDir")
    {
        Description = "Path to models";
        Arity = ArgumentArity.ZeroOrOne;
        this.AcceptExistingOnly();
    }
}
