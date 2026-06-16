namespace Anonymizer.Python;

internal sealed record Result(int ExitCode, string Output, string Error)
{
    public bool IsSuccess => ExitCode == 0;
}
