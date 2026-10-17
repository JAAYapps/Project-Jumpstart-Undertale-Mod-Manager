using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Project_Jumpstart_Undertale_Mod_Manager.Utilities;

public static class OneClickPipe
{
    private static string PipeName => "pjum-oneclick-" + Environment.UserName;

    public static bool TrySend(string link)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client);
            writer.WriteLine(link);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Listen(Action<string> onLink)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? line = reader.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                        onLink(line.Trim());
                }
                catch (IOException)
                {
                    Thread.Sleep(1000);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PJUM 1-Click listener"
        };
        thread.Start();
    }
}
