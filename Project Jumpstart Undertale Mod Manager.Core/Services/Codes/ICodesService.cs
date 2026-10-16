namespace Project_Jumpstart_Undertale_Mod_Manager.Services.Codes;

public interface ICodesService
{
    string CodesFileFor(string gameName);
    IReadOnlyList<CheatCode> LoadCodes(string gameName);
    void WriteCodesMod(IEnumerable<CheatCode> codes, string modDirectory);
}
