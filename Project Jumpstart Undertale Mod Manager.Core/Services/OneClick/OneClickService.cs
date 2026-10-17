using System.Diagnostics;
using Microsoft.Win32;

namespace Project_Jumpstart_Undertale_Mod_Manager.Services.OneClick;

public class OneClickService : IOneClickService
{
    private readonly HttpMessageHandler? _handler;
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly string _linuxApplicationsFolder;
    private readonly Action<string, string[]> _run;

    public string Scheme => "pjum";

    public OneClickService() : this(null, ["gamebanana.com"],
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "applications"), Run)
    {
    }

    public OneClickService(HttpMessageHandler? handler, IReadOnlyList<string> allowedHosts, string linuxApplicationsFolder, Action<string, string[]> run)
    {
        _handler = handler;
        _allowedHosts = allowedHosts;
        _linuxApplicationsFolder = linuxApplicationsFolder;
        _run = run;
    }

    public bool TryParse(string argument, out OneClickRequest? request, out string error)
    {
        request = null;
        string prefix = Scheme + ":";
        string text = argument.Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{argument}' is not a {prefix} link.";
            return false;
        }

        string link = text[prefix.Length..].TrimStart('/');
        int comma = link.IndexOf(',');
        if (comma >= 0)
            link = link[..comma];
        link = Uri.UnescapeDataString(link);

        if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            error = $"'{link}' is not a web address.";
            return false;
        }
        if (!_allowedHosts.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"1-Click links can only download from {string.Join(", ", _allowedHosts)}, not {url.Host}.";
            return false;
        }

        request = new OneClickRequest(url, argument);
        error = "";
        return true;
    }

    public async Task<string> DownloadAsync(OneClickRequest request, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        using var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectJumpstartUndertaleModManager/1.0");
        using HttpResponseMessage response = await client.GetAsync(request.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        string name = response.Content.Headers.ContentDisposition?.FileNameStar
                      ?? response.Content.Headers.ContentDisposition?.FileName
                      ?? Path.GetFileName(response.RequestMessage?.RequestUri?.LocalPath ?? "")
                      ?? "";
        name = string.Concat(name.Trim('"').Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(name))
            name = "mod";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name += ".zip";

        string path = Path.Combine(folder, name);
        await using (Stream body = await response.Content.ReadAsStreamAsync(ct))
        await using (FileStream file = File.Create(path))
            await body.CopyToAsync(file, ct);

        byte[] start = new byte[4];
        int read;
        await using (FileStream check = File.OpenRead(path))
            read = await check.ReadAsync(start, ct);
        if (read < 4 || start[0] != (byte)'P' || start[1] != (byte)'K')
        {
            string kind = read >= 2 && start[0] == (byte)'7' && start[1] == (byte)'z' ? "a .7z archive"
                        : read >= 4 && start[0] == (byte)'R' && start[1] == (byte)'a' && start[2] == (byte)'r' ? "a .rar archive"
                        : "not a zip file";
            File.Delete(path);
            throw new InvalidDataException($"The download is {kind}. Only .zip mods can be added for now.");
        }
        return path;
    }

    public string RegisterScheme(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            key.SetValue("", "URL:Project Jumpstart Undertale Mod Manager");
            key.SetValue("URL Protocol", "");
            using RegistryKey command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{executablePath}\" \"%1\"");
            return $@"Registered {Scheme}: links in HKEY_CURRENT_USER\Software\Classes\{Scheme}.";
        }

        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(_linuxApplicationsFolder);
            string desktopName = $"{Scheme}-handler.desktop";
            string desktopFile = Path.Combine(_linuxApplicationsFolder, desktopName);
            File.WriteAllText(desktopFile,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Project Jumpstart Undertale Mod Manager\n" +
                $"Exec=\"{executablePath}\" %u\n" +
                "Terminal=false\n" +
                "NoDisplay=true\n" +
                $"MimeType=x-scheme-handler/{Scheme};\n");
            _run("xdg-mime", ["default", desktopName, $"x-scheme-handler/{Scheme}"]);
            _run("update-desktop-database", [_linuxApplicationsFolder]);
            return $"Registered {Scheme}: links with {desktopFile}.";
        }

        throw new PlatformNotSupportedException("1-Click links can only be registered on Windows and Linux for now.");
    }

    private static void Run(string program, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
