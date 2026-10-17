namespace Project_Jumpstart_Undertale_Mod_Manager.Services.ModPackage;

public enum IntegrityResult
{
    NoHash,
    Match,
    Mismatch
}

public interface IModPackageService
{
    string ComputeIntegrityHash(string modDirectory);
    string PackageMod(string modDirectory, string zipPath);
    IntegrityResult CheckIntegrity(string modDirectory);
}
