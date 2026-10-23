using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace Stransfer.App.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    private readonly string name;
    public bool IsFirst { get; }
    public SingleInstance(string? channelSuffix = null)
    {
        name = "Stransfer.v2." + WindowsIdentity.GetCurrent().User!.Value + "." + Process.GetCurrentProcess().SessionId;
        var profile = channelSuffix ?? Environment.GetEnvironmentVariable("STRANSFER_STATE_DIR");
        if (profile is not null) name += "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile)))[..16];
        mutex = new Mutex(true, @"Local\" + name, out var first); IsFirst = first;
    }
    public async Task SendAsync(string[] arguments)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (bytes.Length > 128 * 1024) throw new IOException("The command is too long.");
        await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length)); await pipe.WriteAsync(bytes); await pipe.FlushAsync();
    }
    public async Task ListenAsync(Action<string[]> receive)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token);
                var length = BitConverter.ToInt32(header);
                if (length < 0 || length > 128 * 1024) continue;
                var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, timeout.Token);
                var arguments = JsonSerializer.Deserialize<string[]>(bytes);
                if (arguments is not null) receive(arguments);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or JsonException) { }
        }
    }
    public void Dispose() { stop.Cancel(); mutex.Dispose(); }
}
