namespace Project_Jumpstart_Undertale_Mod_Manager.Services.OneClick;

public sealed record OneClickRequest(Uri DownloadUrl, string Raw);

public interface IOneClickService
{
    string Scheme { get; }
    bool TryParse(string argument, out OneClickRequest? request, out string error);
    Task<string> DownloadAsync(OneClickRequest request, string folder, CancellationToken ct = default);
    string RegisterScheme(string executablePath);
}
