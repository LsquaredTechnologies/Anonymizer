using Anonymizer.Cli;
using Anonymizer.Cli.Commands;
using Anonymizer.Cli.Downloaders;
using Anonymizer.Cli.Lifetime;
using Anonymizer.Cli.Python;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddSingleton<PIIModelDownloader>();
    services.AddHttpClient<FaceModelDownloader>();
    services.AddSingleton((sp) => ActivatorUtilities.CreateInstance<UVRunner>(sp, Application.Tools.UV.File));
    services.AddSingleton<AnonymizerService>();

    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is "setup")
    {
        if (OperatingSystem.IsWindows())
            services.AddSingleton<IAutostartManager, WindowsAutostart>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IAutostartManager, LinuxAutostart>();
    }
});
builder.ConfigureAppConfiguration((config) =>
    config.AddJsonFile(Application.AppSettings.Path, optional: true, reloadOnChange: true));
builder.ConfigureLogging((builder) =>
{
    builder.ClearProviders();
    builder.AddFilter((category, level) => category?.StartsWith("Microsoft.") is false || level >= LogLevel.Error);
    builder.AddSimpleConsole((options) =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
        options.IncludeScopes = false;
    });
});

RootCommand root = new(builder);
var result = root.Parse(args, new() { EnablePosixBundling = true });
return await result.InvokeAsync();



//string pdfFilePath = args.Length > 0 ? args[0] : throw new Exception("Veuillez fournir le chemin du fichier à analyser en argument.");
//string modelsDir = args.Length > 1 ? args[1] : "./models";

//var doc = PDFDocumentTextExtractor.Extract(pdfFilePath);
//foreach (var page in doc.PhysicalTree.Pages)
//{
//    Console.WriteLine($"Page {page.PageNumber}");
//    foreach (var horizontalZone in page.HorizontalZones)
//    {
//        Console.WriteLine($"HorizontalZone : {horizontalZone.Id}");
//        foreach (var column in horizontalZone.Columns)
//        {
//            Console.WriteLine($"Column : {column.Id}");
//            foreach (var verticalZone in column.VerticalZones)
//            {
//                Console.WriteLine($"VerticalZone : {verticalZone.Id}");
//                foreach (var line in verticalZone.Lines)
//                {
//                    Console.WriteLine($"Line : {line.Text}");
//                }
//            }
//        }
//    }
//}
//Console.WriteLine();
//foreach (var para in doc.SemanticTree.Paragraphs)
//    Console.WriteLine($"Para : {para.Text}");

//OnnxPiiDetector detector = new(new(modelsDir), "pii-gliner");

//Console.WriteLine($"Analyse du fichier : {pdfFilePath}...");
//var entities = await detector.AnalyzeMarkdownFileAsync(new(pdfFilePath));

//Console.WriteLine("\nEntités détectées :");
//foreach (var e in entities)
//{
//    Console.WriteLine($"- {e.Label,-6} [{e.Start,3}-{e.End,-3}] (score: {e.Score:0.000}) -> \"{e.Text}\"");
//}

//Console.WriteLine("\nFormat JSON :");
//Console.WriteLine(JsonSerializer.Serialize(entities, Entity.JsonContext));
