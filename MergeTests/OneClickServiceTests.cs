using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Project_Jumpstart_Undertale_Mod_Manager.Services.OneClick;
using Xunit;

namespace MergeTests;

public class OneClickServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pjum_oneclick_test_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _ran = [];

    public OneClickServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeHandler(byte[] body, string? fileName) : HttpMessageHandler
    {
        public Uri? Requested;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request };
            if (fileName is not null)
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };
            return Task.FromResult(response);
        }
    }

    private OneClickService Service(HttpMessageHandler? handler = null) =>
        new(handler, ["gamebanana.com"], Path.Combine(_root, "applications"), (program, args) => _ran.Add(program + " " + string.Join(" ", args)));

    private static byte[] ZipBytes()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("mod.json").Open());
            writer.Write("{ \"Name\": \"Test\" }");
        }
        return memory.ToArray();
    }

    [Theory]
    [InlineData("pjum:https://gamebanana.com/mmdl/906564", "https://gamebanana.com/mmdl/906564")]
    [InlineData("PJUM:https://gamebanana.com/mmdl/906564", "https://gamebanana.com/mmdl/906564")]
    [InlineData("pjum:https://gamebanana.com/mmdl/906564,Mod,123456", "https://gamebanana.com/mmdl/906564")]
    [InlineData("pjum://https://gamebanana.com/mmdl/906564", "https://gamebanana.com/mmdl/906564")]
    [InlineData("pjum:https%3A%2F%2Fgamebanana.com%2Fmmdl%2F906564", "https://gamebanana.com/mmdl/906564")]
    [InlineData("pjum:https://files.gamebanana.com/mods/test.zip", "https://files.gamebanana.com/mods/test.zip")]
    public void Good_links_give_the_download_address(string link, string expected)
    {
        Assert.True(Service().TryParse(link, out OneClickRequest? request, out string error), error);
        Assert.Equal(expected, request!.DownloadUrl.ToString());
    }

    [Theory]
    [InlineData("r2:https://gamebanana.com/mmdl/906564", "is not a pjum: link")]
    [InlineData("pjum:https://example.com/mod.zip", "not example.com")]
    [InlineData("pjum:https://gamebanana.com.example.com/mod.zip", "not gamebanana.com.example.com")]
    [InlineData("pjum:https://notgamebanana.com/mod.zip", "not notgamebanana.com")]
    [InlineData("pjum:ftp://gamebanana.com/mod.zip", "is not a web address")]
    [InlineData("pjum:mod.zip", "is not a web address")]
    public void Other_links_are_refused_with_a_reason(string link, string expected)
    {
        Assert.False(Service().TryParse(link, out OneClickRequest? request, out string error));
        Assert.Null(request);
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task A_zip_download_is_saved_under_its_own_name()
    {
        var handler = new FakeHandler(ZipBytes(), "Sonic over Frisk.zip");
        OneClickService service = Service(handler);
        service.TryParse("pjum:https://gamebanana.com/mmdl/1", out OneClickRequest? request, out _);

        string path = await service.DownloadAsync(request!, Path.Combine(_root, "downloads"));

        Assert.Equal("https://gamebanana.com/mmdl/1", handler.Requested!.ToString());
        Assert.Equal("Sonic over Frisk.zip", Path.GetFileName(path));
        using ZipArchive zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("mod.json"));
    }

    [Theory]
    [InlineData(new byte[] { (byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C }, "a .7z archive")]
    [InlineData(new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07 }, "a .rar archive")]
    [InlineData(new byte[] { (byte)'<', (byte)'h', (byte)'t', (byte)'m', (byte)'l', (byte)'>' }, "not a zip file")]
    public async Task A_download_that_is_not_a_zip_is_refused_and_deleted(byte[] body, string expected)
    {
        OneClickService service = Service(new FakeHandler(body, "mod.7z"));
        service.TryParse("pjum:https://gamebanana.com/mmdl/1", out OneClickRequest? request, out _);
        string downloads = Path.Combine(_root, "downloads");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(request!, downloads));

        Assert.Contains(expected, ex.Message);
        Assert.Empty(Directory.GetFiles(downloads));
    }

    [Fact]
    public async Task A_download_without_a_name_still_ends_in_zip()
    {
        OneClickService service = Service(new FakeHandler(ZipBytes(), null));
        service.TryParse("pjum:https://gamebanana.com/mmdl/906564", out OneClickRequest? request, out _);

        string path = await service.DownloadAsync(request!, Path.Combine(_root, "downloads"));

        Assert.Equal("906564.zip", Path.GetFileName(path));
    }

    [Fact]
    public void Registering_on_Linux_writes_a_desktop_entry_for_the_pjum_scheme()
    {
        if (!OperatingSystem.IsLinux()) return;

        string message = Service().RegisterScheme("/opt/PJUM/Project Jumpstart Undertale Mod Manager");

        string file = Path.Combine(_root, "applications", "pjum-handler.desktop");
        string text = File.ReadAllText(file);
        Assert.Contains("Exec=\"/opt/PJUM/Project Jumpstart Undertale Mod Manager\" %u\n", text);
        Assert.Contains("MimeType=x-scheme-handler/pjum;\n", text);
        Assert.Contains(file, message);
        Assert.Equal(new[] { "xdg-mime default pjum-handler.desktop x-scheme-handler/pjum", "update-desktop-database " + Path.Combine(_root, "applications") }, _ran);
    }
}
