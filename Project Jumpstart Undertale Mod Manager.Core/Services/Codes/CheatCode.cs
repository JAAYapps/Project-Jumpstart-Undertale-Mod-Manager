using System.Text.Json.Serialization;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;

public sealed class CheatCode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("patches")]
    public List<CheatCodePatch> Patches { get; set; } = [];
}

public sealed class CheatCodePatch
{
    [JsonPropertyName("entry")]
    public string Entry { get; set; } = "";

    [JsonPropertyName("op")]
    public string? Operation { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("find")]
    public string? Find { get; set; }

    [JsonPropertyName("replace")]
    public string? Replace { get; set; }
}
