using System.Text.Json;
using System.Text.Json.Serialization;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Code;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;

public class CodesService : ICodesService
{
    public const string ModName = "(Codes)";

    private readonly string _codesFolder;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CodesService() : this(Path.Combine(AppContext.BaseDirectory, "codes"))
    {
    }

    public CodesService(string codesFolder)
    {
        _codesFolder = codesFolder;
    }

    public string CodesFileFor(string gameName) => Path.Combine(_codesFolder, gameName + ".json");

    public IReadOnlyList<CheatCode> LoadCodes(string gameName)
    {
        string file = CodesFileFor(gameName);
        if (!File.Exists(file))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<CheatCode>>(File.ReadAllText(file), ReadOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{file} is not valid JSON: {ex.Message}", ex);
        }
    }

    public void WriteCodesMod(IEnumerable<CheatCode> codes, string modDirectory)
    {
        var files = new Dictionary<string, CodePatchJson>(StringComparer.OrdinalIgnoreCase);

        foreach (CheatCode code in codes)
        {
            foreach (CheatCodePatch patch in code.Patches)
            {
                string rel = $"{code.Target.Trim().Trim('/')}/data.win/code/{patch.Entry}.json";
                if (string.IsNullOrWhiteSpace(code.Target) || string.IsNullOrWhiteSpace(patch.Entry) ||
                    !ModAddressParser.TryParse(rel, out ModAddress? addr, out string error) ||
                    addr.Category != AssetCategory.Code || addr.AssetName != patch.Entry)
                    throw new InvalidDataException(
                        $"Code '{code.Name}' has target '{code.Target}' and entry '{patch.Entry}', which don't make a mod path. " +
                        "Use a target such as deltarune/chapter2 or undertale, and a code entry name such as gml_Object_obj_time_Step_0.");

                if (!files.TryGetValue(rel, out CodePatchJson? json))
                    files[rel] = json = new CodePatchJson();
                json.Operations.Add(new CodePatchOperation
                {
                    Operation = patch.Operation,
                    Code = patch.Code,
                    Find = patch.Find,
                    Replace = patch.Replace
                });
            }
        }

        Directory.CreateDirectory(modDirectory);
        File.WriteAllText(Path.Combine(modDirectory, "mod.json"),
            JsonSerializer.Serialize(new { name = ModName, version = "1.0.0" }, WriteOptions));

        foreach ((string rel, CodePatchJson json) in files)
        {
            string path = Path.Combine(modDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(json, WriteOptions));
        }
    }
}
