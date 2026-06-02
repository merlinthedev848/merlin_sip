using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Threading;

namespace MerlinSip.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Global\\CKMediaServices.MerlinSIP.SingleInstance";
    private const string PipeName = "CKMediaServices.MerlinSIP.Activate";
    private readonly Mutex _mutex;
    private CancellationTokenSource? _listenCancellation;

    public bool IsPrimaryInstance { get; }

    public event EventHandler? ActivationRequested;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public static async Task NotifyExistingInstanceAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(1200);
            await using var writer = new StreamWriter(pipe);
            await writer.WriteLineAsync("activate");
            await writer.FlushAsync();
        }
        catch (Exception error)
        {
            DebugLog.Write($"SINGLE INSTANCE notify failed error={error.Message}");
        }
    }

    public void StartListening(Dispatcher dispatcher)
    {
        if (!IsPrimaryInstance || _listenCancellation is not null)
        {
            return;
        }

        _listenCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(dispatcher, _listenCancellation.Token));
    }

    private async Task ListenLoopAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe);
                _ = await reader.ReadLineAsync(cancellationToken);
                await dispatcher.InvokeAsync(() => ActivationRequested?.Invoke(this, EventArgs.Empty));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                DebugLog.Write($"SINGLE INSTANCE listen failed error={error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _listenCancellation?.Cancel();
        _listenCancellation?.Dispose();
        if (IsPrimaryInstance)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
