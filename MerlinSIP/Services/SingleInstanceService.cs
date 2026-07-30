using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MerlinSip.Services;

public sealed class SingleInstanceService : IDisposable
{
	private const string MutexName = "Global\\CKMediaServices.MerlinSIP.SingleInstance";

	private const string PipeName = "CKMediaServices.MerlinSIP.Activate";

	private readonly Mutex _mutex;

	private CancellationTokenSource? _listenCancellation;

	public bool IsPrimaryInstance { get; }

	public event EventHandler<string>? ActivationRequested;

	public SingleInstanceService()
	{
		_mutex = new Mutex(initiallyOwned: true, "Global\\CKMediaServices.MerlinSIP.SingleInstance", out var createdNew);
		IsPrimaryInstance = createdNew;
	}

	public static async Task NotifyExistingInstanceAsync(string message = "activate")
	{
		_ = 4;
		try
		{
			await using NamedPipeClientStream pipe = new NamedPipeClientStream(".", "CKMediaServices.MerlinSIP.Activate", PipeDirection.Out);
			await pipe.ConnectAsync(1200);
			await using StreamWriter writer = new StreamWriter(pipe);
			await writer.WriteLineAsync(message);
			await writer.FlushAsync();
		}
		catch (Exception ex)
		{
			DebugLog.Write("SINGLE INSTANCE notify failed error=" + ex.Message);
		}
	}

	public void StartListening(Dispatcher dispatcher)
	{
		if (IsPrimaryInstance && _listenCancellation == null)
		{
			_listenCancellation = new CancellationTokenSource();
			Task.Run(() => ListenLoopAsync(dispatcher, _listenCancellation.Token));
		}
	}

	private async Task ListenLoopAsync(Dispatcher dispatcher, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await using NamedPipeServerStream pipe = new NamedPipeServerStream("CKMediaServices.MerlinSIP.Activate", PipeDirection.In, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
				await pipe.WaitForConnectionAsync(cancellationToken);
				using StreamReader reader = new StreamReader(pipe);
				string msg = await reader.ReadLineAsync(cancellationToken);
				await dispatcher.InvokeAsync(delegate
				{
					this.ActivationRequested?.Invoke(this, msg ?? "activate");
				});
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				DebugLog.Write("SINGLE INSTANCE listen failed error=" + ex2.Message);
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
