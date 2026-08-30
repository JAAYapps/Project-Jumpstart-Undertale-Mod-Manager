using System.Text.Json;
using System.Text.Json.Serialization;
using Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Addressing;
using UndertaleModLib.Compiler;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Merge.Code;

public sealed class CodePatchOperation
{
    [JsonPropertyName("op")]
    public string? Operation { get; set; }
    
    [JsonPropertyName("code")]
    public string? Code { get; set; }
    
    [JsonPropertyName("find")]
    public string? Find { get; set; }
    
    [JsonPropertyName("replace")]
    public string? Replace { get; set; }
}

public sealed class CodePatchJson
{
    [JsonPropertyName("operations")]
    public List<CodePatchOperation> Operations { get; set; } = [];
}

public static class CodeImporter
{
    public static void Apply(CodeImportGroup group, ModAddress addr, string jsonFile)
    {
        CodePatchJson json = ReadJson(jsonFile);

        foreach (var operation in json.Operations)
            ApplyOperation(group, operation, addr);
    }

    private static void ApplyOperation(CodeImportGroup group, CodePatchOperation operation, ModAddress addr)
    {
        switch (operation.Operation)
        {
            case "append":
                if (string.IsNullOrEmpty(operation.Code))
                    throw new InvalidOperationException($"Code operation '{operation.Operation}' parsed to null in mod {addr.AssetName}.");
                if (!string.IsNullOrEmpty(operation.Find))
                    throw new InvalidOperationException($"Find is not a valid attribute in Code operation '{operation.Operation}' in mod {addr.AssetName}.");
                if (!string.IsNullOrEmpty(operation.Replace))
                    throw new InvalidOperationException($"Replace is not a valid attribute in Code operation '{operation.Operation}' in mod {addr.AssetName}.");
                
                group.QueueAppend(addr.AssetName, operation.Code);
                break;
            case "find":
                if (!string.IsNullOrEmpty(operation.Code))
                    throw new InvalidOperationException($"Code is not a valid attribute in Code operation '{operation.Operation}' in mod {addr.AssetName}.");
                if (string.IsNullOrEmpty(operation.Find) || operation.Replace is null)
                    throw new InvalidOperationException($"Code operation '{operation.Operation}' is missing the find attribute in mod {addr.AssetName}.");

                group.QueueFindReplace(addr.AssetName, operation.Find, operation.Replace);
                break;
            case "prepend":
                if (string.IsNullOrEmpty(operation.Code))
                    throw new InvalidOperationException($"Code operation '{operation.Operation}' parsed to null in mod {addr.AssetName}.");
                if (!string.IsNullOrEmpty(operation.Find))
                    throw new InvalidOperationException($"Find is not a valid attribute in Code operation '{operation.Operation}' in mod {addr.AssetName}.");
                if (!string.IsNullOrEmpty(operation.Replace))
                    throw new InvalidOperationException($"Replace is not a valid attribute in Code operation '{operation.Operation}' in mod {addr.AssetName}.");
                
                group.QueuePrepend(addr.AssetName, operation.Code);
                break;
            default:
                throw new InvalidOperationException($"Code operation '{operation.Operation}' is invalid in mod " + addr.AssetName);
        }
    }
    
    private static CodePatchJson ReadJson(string file)
    {
        try
        {
            CodePatchJson? json = JsonSerializer.Deserialize<CodePatchJson>(
                File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (json is null)
                throw new InvalidOperationException($"Code JSON '{file}' parsed to null.");
            return json;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Code JSON '{file}' is invalid: {ex.Message}", ex);
        }
    }
}