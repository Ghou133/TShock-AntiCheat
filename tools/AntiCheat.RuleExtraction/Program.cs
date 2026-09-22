using System.Text.Json;
using AntiCheat.RuleExtraction;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/AntiCheat.RuleExtraction -- <repository-root> <output-directory>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var result = SourceExtractor.Extract(
    File.ReadAllText(Path.Combine(root, "reference/mklp/MKLP/Modules/SurvivalManager.cs")),
    File.ReadAllText(Path.Combine(root, "reference/mklp/MKLP/Config.cs")));
Directory.CreateDirectory(output);
var options = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(Path.Combine(output, "mklp-operations.json"), result.ToJsonString(options) + Environment.NewLine);
File.WriteAllText(Path.Combine(output, "candidates.json"), CandidateBuilder.Build(result).ToJsonString(options) + Environment.NewLine);
Console.WriteLine(result["summary"]!.ToJsonString(options));
return 0;
