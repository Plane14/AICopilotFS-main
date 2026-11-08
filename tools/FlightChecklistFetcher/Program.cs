// Pre-fetches every aircraft page from flightchecklist.app through
// FlightChecklistProvider and writes the resulting cache JSON files into
// src/MSFS2024AIPilot.Gui/flightchecklists/ so they ship inside the app.
//
// Usage: dotnet run --project tools/FlightChecklistFetcher

using System.Text.Json;
using MSFS2024AIPilot.Core;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var outDir = Path.Combine(repoRoot, "src", "MSFS2024AIPilot.Gui", "flightchecklists");
Directory.CreateDirectory(outDir);

var index = await FlightChecklistProvider.GetAircraftIndexAsync();
Console.WriteLine($"Index: {index.Count} aircraft");

var cacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "AICopilotFS", "flightchecklists");
Directory.CreateDirectory(cacheDir);

var ok = 0; var fail = 0;
foreach (var (name, slug) in index)
{
    try
    {
        var bundle = await FlightChecklistProvider.FetchBySlugAsync(slug);
        var src = Path.Combine(cacheDir, $"{slug}.json");
        var dst = Path.Combine(outDir, $"{slug}.json");
        if (File.Exists(src))
        {
            File.Copy(src, dst, overwrite: true);
            var steps = bundle?.Checklists?.Lists.Sum(l => l.Items.Count) ?? 0;
            var specs = bundle?.Performance is not null ? "specs" : "no-specs";
            Console.WriteLine($"  {slug,-28} {steps,4} steps  {specs}");
            ok++;
        }
        else
        {
            Console.WriteLine($"  {slug,-28} NO CACHE FILE");
            fail++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {slug,-28} ERROR {ex.Message}");
        fail++;
    }
}

// Ship the index too so slug matching works fully offline.
var idxSrc = Path.Combine(cacheDir, "_index.json");
if (File.Exists(idxSrc))
    File.Copy(idxSrc, Path.Combine(outDir, "_index.json"), overwrite: true);

Console.WriteLine($"Done: {ok} ok, {fail} failed -> {outDir}");
return fail > index.Count / 2 ? 1 : 0;
