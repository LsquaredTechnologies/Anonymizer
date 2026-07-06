using System.Runtime.InteropServices;

namespace Anonymizer.Core;

internal static unsafe partial class NativeMethods
{
    [LibraryImport("Anonymizer.Core", EntryPoint = "redact_pdf", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RedactPdf(string inputPath, string modelsDir, string outputPath);
}
