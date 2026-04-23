using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace NvimWinFormsGui;

internal static class Program
{
    private const string MutexName = "NvimWinFormsGui.Singleton";
    private const string PipeName = "NvimWinFormsGui.OpenFilePipe";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);

        if (!isFirstInstance)
        {
            SendArgsToExistingInstance(args);
            return;
        }

        ApplicationConfiguration.Initialize();

        var form = new MainForm(args);
        form.StartPipeServer(PipeName);

        Application.Run(form);
    }

    private static void SendArgsToExistingInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);

            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };

            foreach (var arg in NormalizeArgs(args))
                writer.WriteLine(arg);
        }
        catch
        {
            // 既存インスタンスへ送れなかった場合は何もしない
        }
    }

    private static List<string> NormalizeArgs(string[] args)
    {
        var result = new List<string>();

        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            try
            {
                result.Add(Path.GetFullPath(arg));
            }
            catch
            {
                result.Add(arg);
            }
        }

        return result;
    }
}