using Anonymizer.Cli.Commands;

Console.OutputEncoding = System.Text.Encoding.UTF8;

RootCommand root = new();
var result = root.Parse(args, new() { EnablePosixBundling = true });
return await result.InvokeAsync();
