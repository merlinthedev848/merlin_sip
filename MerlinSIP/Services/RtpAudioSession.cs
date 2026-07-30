using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MerlinSip.Models;

namespace MerlinSip.Services;

public sealed class RtpAudioSession : IDisposable
{
	private const int SampleRate = 8000;

	private const int SamplesPerPacket = 160;

	private readonly MediaDeviceInfo _inputDevice;

	private readonly MediaDeviceInfo _outputDevice;

	private readonly double _inputGain;

	private readonly double _outputGain;

	private UdpClient _rtpClient;

	private UdpClient? _secondaryRtpClient;

	private readonly List<AudioBuffer> _inputBuffers = new List<AudioBuffer>();

	private readonly List<AudioBuffer> _outputBuffers = new List<AudioBuffer>();

	private readonly WinMm.WaveInProc _waveInCallback;

	private readonly object _sendSync = new object();

	private readonly object _secondarySendSync = new object();

	private readonly object _buffersSync = new object();

	private nint _waveIn;

	private nint _waveOut;

	private IPEndPoint? _remoteEndPoint;

	private IPEndPoint? _secondaryRemoteEndPoint;

	private CancellationTokenSource? _receiveCancellation;

	private CancellationTokenSource? _secondaryReceiveCancellation;

	private ushort _sequence;

	private ushort _secondarySequence;

	private uint _timestamp;

	private uint _secondaryTimestamp;

	private readonly uint _ssrc = (uint)Random.Shared.Next();

	private readonly uint _secondarySsrc = (uint)Random.Shared.Next();

	private int _payloadType;

	private int _secondaryPayloadType;

	private volatile bool _running;

	private volatile bool _secondaryRunning;

	private volatile bool _devicesPrepared;

	private volatile bool _transmitMicrophone;

	private volatile bool _muted;

	private volatile bool _call1Held;

	private volatile bool _call2Held;

	private volatile bool _conferenceMerged;

	private readonly AudioFrameQueue _call1RxQueue = new AudioFrameQueue();

	private readonly AudioFrameQueue _call2RxQueue = new AudioFrameQueue();

	private readonly byte[] _rawPcmBuffer = new byte[4096];

	private readonly short[] _micSampleBuffer = new short[2048];

	private readonly byte[] _speakerPcmBuffer = new byte[4096];

	private readonly byte[] _payload1Buffer = new byte[2048];

	private readonly byte[] _payload2Buffer = new byte[2048];

	private int _receivedPackets;

	private int _sentPackets;

	private long _expectedPackets;

	private ushort _lastRxSequence;

	private FileStream? _recordingStream;

	private BinaryWriter? _recordingWriter;

	private readonly object _recordingSync = new object();

	private int _recordedDataBytes;

	public int LocalPort { get; }

	public int SecondaryLocalPort { get; private set; }

	public int ReceivedPackets => Volatile.Read(in _receivedPackets);

	public int SentPackets => Volatile.Read(in _sentPackets);

	public float CurrentMicLevel { get; private set; }

	public bool IsRecording { get; private set; }

	public string? CurrentRecordingPath { get; private set; }

	public bool IsConferenceMerged => _conferenceMerged;

	public RtpAudioSession(MediaDeviceInfo inputDevice, MediaDeviceInfo outputDevice, double inputGain = 1.0, double outputGain = 1.0)
	{
		_inputDevice = inputDevice;
		_outputDevice = outputDevice;
		_inputGain = Math.Clamp(inputGain, 0.25, 2.0);
		_outputGain = Math.Clamp(outputGain, 0.25, 2.0);
		_waveInCallback = WaveInCallback;
		_rtpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
		LocalPort = ((IPEndPoint)_rtpClient.Client.LocalEndPoint).Port;
	}

	public int PrepareSecondarySocket()
	{
		if (_secondaryRtpClient == null)
		{
			_secondaryRtpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
			SecondaryLocalPort = ((IPEndPoint)_secondaryRtpClient.Client.LocalEndPoint).Port;
		}
		return SecondaryLocalPort;
	}

	public void PrepareDevices()
	{
		if (!_devicesPrepared)
		{
			OpenWaveIn();
			_devicesPrepared = true;
			DebugLog.Write($"RTP devices prepared input={_inputDevice.Name} output={_outputDevice.Name} localPort={LocalPort}");
		}
	}

	public async Task StartAsync(string remoteAddress, int remotePort, int payloadType, bool transmitMicrophone = true)
	{
		if (_running)
		{
			_remoteEndPoint = new IPEndPoint(await ResolveRemoteAddressAsync(remoteAddress), remotePort);
			_payloadType = payloadType;
			_transmitMicrophone = transmitMicrophone;
			if (transmitMicrophone && _waveIn != IntPtr.Zero)
			{
				WinMm.waveInStart(_waveIn);
			}
			return;
		}
		_remoteEndPoint = new IPEndPoint(await ResolveRemoteAddressAsync(remoteAddress), remotePort);
		_payloadType = payloadType;
		_transmitMicrophone = transmitMicrophone;
		PrepareDevices();
		_running = true;
		_receiveCancellation = new CancellationTokenSource();
		Task.Run(() => ReceiveLoopAsync(_rtpClient, _call1RxQueue, _receiveCancellation.Token, isSecondary: false));
		if (transmitMicrophone && _waveIn != IntPtr.Zero)
		{
			WinMm.waveInStart(_waveIn);
		}
	}

	public async Task StartSecondaryAsync(string remoteAddress, int remotePort, int payloadType)
	{
		PrepareSecondarySocket();
		_secondaryRemoteEndPoint = new IPEndPoint(await ResolveRemoteAddressAsync(remoteAddress), remotePort);
		_secondaryPayloadType = payloadType;
		_secondaryRunning = true;
		_secondaryReceiveCancellation = new CancellationTokenSource();
		Task.Run(() => ReceiveLoopAsync(_secondaryRtpClient, _call2RxQueue, _secondaryReceiveCancellation.Token, isSecondary: true));
		DebugLog.Write($"RTP secondary start remote={_secondaryRemoteEndPoint} port={SecondaryLocalPort}");
	}

	public void MergeConference()
	{
		_call1Held = false;
		_call2Held = false;
		_conferenceMerged = true;
		DebugLog.Write("RTP audio conference merged.");
	}

	public void UnmergeConference()
	{
		_conferenceMerged = false;
		DebugLog.Write("RTP audio conference unmerged.");
	}

	public void PromoteSecondaryToPrimary()
	{
		lock (_buffersSync)
		{
			_conferenceMerged = false;
			_call1Held = false;
			_call2Held = false;
			if (_secondaryRemoteEndPoint != null)
			{
				_remoteEndPoint = _secondaryRemoteEndPoint;
				_payloadType = _secondaryPayloadType;
				_secondaryRemoteEndPoint = null;
			}
			if (_secondaryRtpClient != null)
			{
				_rtpClient?.Dispose();
				_rtpClient = _secondaryRtpClient;
				_secondaryRtpClient = null;
			}
			_running = true;
			_secondaryRunning = false;
			_call2RxQueue.Clear();
			DebugLog.Write("RTP promoted secondary call to primary session.");
		}
	}

	public void StopSecondary()
	{
		_conferenceMerged = false;
		_secondaryRunning = false;
		_call2Held = false;
		_secondaryReceiveCancellation?.Cancel();
		_call2RxQueue.Clear();
		_secondaryRemoteEndPoint = null;
		DebugLog.Write("RTP secondary stopped");
	}

	public void Stop()
	{
		StopRecording();
		StopSecondary();
		_running = false;
		_receiveCancellation?.Cancel();
		_call1RxQueue.Clear();
		DebugLog.Write("RTP stop");
		if (_waveIn != IntPtr.Zero)
		{
			WinMm.waveInStop(_waveIn);
			WinMm.waveInReset(_waveIn);
		}
		if (_waveOut != IntPtr.Zero)
		{
			WinMm.waveOutReset(_waveOut);
		}
		_remoteEndPoint = null;
	}

	public void SetMuted(bool muted)
	{
		_muted = muted;
	}

	public void SetHeld(bool held)
	{
		_call1Held = held;
	}

	public void SetSecondaryHeld(bool held)
	{
		_call2Held = held;
	}

	public string StartRecording(string targetNumber)
	{
		lock (_recordingSync)
		{
			if (IsRecording)
			{
				return CurrentRecordingPath ?? "";
			}
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Merlin SIP", "Recordings");
			Directory.CreateDirectory(text);
			string value = string.Concat(targetNumber.Where(char.IsLetterOrDigit));
			if (string.IsNullOrWhiteSpace(value))
			{
				value = "Call";
			}
			string path = $"Call_{value}_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
			CurrentRecordingPath = Path.Combine(text, path);
			_recordingStream = new FileStream(CurrentRecordingPath, FileMode.Create, FileAccess.Write, FileShare.Read);
			_recordingWriter = new BinaryWriter(_recordingStream, Encoding.UTF8);
			_recordingWriter.Write(Encoding.ASCII.GetBytes("RIFF"));
			_recordingWriter.Write(0);
			_recordingWriter.Write(Encoding.ASCII.GetBytes("WAVE"));
			_recordingWriter.Write(Encoding.ASCII.GetBytes("fmt "));
			_recordingWriter.Write(16);
			_recordingWriter.Write((short)1);
			_recordingWriter.Write((short)2);
			_recordingWriter.Write(8000);
			_recordingWriter.Write(32000);
			_recordingWriter.Write((short)4);
			_recordingWriter.Write((short)16);
			_recordingWriter.Write(Encoding.ASCII.GetBytes("data"));
			_recordingWriter.Write(0);
			_recordedDataBytes = 0;
			IsRecording = true;
			DebugLog.Write("Call recording started: " + CurrentRecordingPath);
			return CurrentRecordingPath;
		}
	}

	public string? StopRecording()
	{
		lock (_recordingSync)
		{
			if (!IsRecording || _recordingWriter == null || _recordingStream == null)
			{
				return null;
			}
			try
			{
				_recordingStream.Seek(4L, SeekOrigin.Begin);
				_recordingWriter.Write(_recordedDataBytes + 36);
				_recordingStream.Seek(40L, SeekOrigin.Begin);
				_recordingWriter.Write(_recordedDataBytes);
				_recordingWriter.Flush();
				_recordingWriter.Dispose();
				_recordingStream.Dispose();
			}
			catch (Exception ex)
			{
				DebugLog.Write("Error finalizing call recording: " + ex.Message);
			}
			string currentRecordingPath = CurrentRecordingPath;
			_recordingWriter = null;
			_recordingStream = null;
			IsRecording = false;
			DebugLog.Write("Call recording stopped: " + currentRecordingPath);
			return currentRecordingPath;
		}
	}

	public (double LossPercent, int SignalBars, string QualityText) GetQualityStats()
	{
		long num = ReceivedPackets;
		long num2 = Interlocked.Read(in _expectedPackets);
		if (num2 <= 0 || num <= 0)
		{
			return (LossPercent: 0.0, SignalBars: 5, QualityText: "5/5 Excellent");
		}
		double num3 = Math.Clamp((double)(num2 - num) * 100.0 / (double)num2, 0.0, 100.0);
		int num4 = ((num3 < 1.0) ? 5 : ((num3 < 3.0) ? 4 : ((num3 < 7.0) ? 3 : ((!(num3 < 15.0)) ? 1 : 2))));
		int num5 = num4;
		return (LossPercent: num3, SignalBars: num5, QualityText: num5 switch
		{
			5 => "5/5 Excellent", 
			4 => "4/5 Good", 
			3 => "3/5 Fair", 
			2 => "2/5 Poor", 
			_ => "1/5 Weak Signal", 
		});
	}

	public async Task SendDtmfAsync(char digit, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!_running || _remoteEndPoint == null)
		{
			throw new InvalidOperationException("RTP is not active.");
		}
		byte eventCode = GetDtmfEventCode(digit);
		uint eventTimestamp;
		lock (_sendSync)
		{
			eventTimestamp = _timestamp;
		}
		ushort duration = 0;
		for (int packetIndex = 0; packetIndex < 5; packetIndex++)
		{
			duration += 400;
			SendDtmfPacket(eventCode, duration, eventTimestamp, end: false, packetIndex == 0);
			await Task.Delay(50, cancellationToken);
		}
		duration += 400;
		for (int packetIndex = 0; packetIndex < 3; packetIndex++)
		{
			SendDtmfPacket(eventCode, duration, eventTimestamp, end: true, marker: false);
			await Task.Delay(50, cancellationToken);
		}
	}

	public void Dispose()
	{
		Stop();
		lock (_buffersSync)
		{
			if (_waveIn != IntPtr.Zero)
			{
				foreach (AudioBuffer inputBuffer in _inputBuffers)
				{
					WinMm.waveInUnprepareHeader(_waveIn, inputBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
					inputBuffer.Dispose();
				}
				_inputBuffers.Clear();
				WinMm.waveInClose(_waveIn);
				_waveIn = IntPtr.Zero;
			}
			if (_waveOut != IntPtr.Zero)
			{
				foreach (AudioBuffer outputBuffer in _outputBuffers)
				{
					WinMm.waveOutUnprepareHeader(_waveOut, outputBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
					outputBuffer.Dispose();
				}
				_outputBuffers.Clear();
				WinMm.waveOutClose(_waveOut);
				_waveOut = IntPtr.Zero;
			}
			_devicesPrepared = false;
			_receiveCancellation?.Dispose();
			_secondaryReceiveCancellation?.Dispose();
			_rtpClient.Dispose();
			_secondaryRtpClient?.Dispose();
		}
	}

	private void OpenWaveIn()
	{
		WaveFormat format = WaveFormat.Pcm16Mono8k();
		int deviceId = ParseDeviceId(_inputDevice.Id);
		int num = WinMm.waveInOpen(out _waveIn, deviceId, ref format, _waveInCallback, IntPtr.Zero, 196608u);
		if (num != 0)
		{
			throw new InvalidOperationException($"Unable to open microphone device: {num}");
		}
		for (int i = 0; i < 6; i++)
		{
			AudioBuffer audioBuffer = new AudioBuffer(320);
			_inputBuffers.Add(audioBuffer);
			WinMm.waveInPrepareHeader(_waveIn, audioBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
			WinMm.waveInAddBuffer(_waveIn, audioBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
		}
	}

	private void OpenWaveOut()
	{
		WaveFormat format = WaveFormat.Pcm16Mono8k();
		int deviceId = ParseDeviceId(_outputDevice.Id);
		int num = WinMm.waveOutOpen(out _waveOut, deviceId, ref format, IntPtr.Zero, IntPtr.Zero, 0u);
		if (num != 0)
		{
			throw new InvalidOperationException($"Unable to open speaker device: {num}");
		}
	}

	private void WaveInCallback(nint waveIn, uint message, nint instance, nint headerPointer, nint reserved)
	{
		try
		{
			if (message != 960 || !_running || _remoteEndPoint == null || _waveIn == IntPtr.Zero)
			{
				return;
			}
			WaveHeader waveHeader = Marshal.PtrToStructure<WaveHeader>(headerPointer);
			if (waveHeader.BytesRecorded <= 0)
			{
				lock (_buffersSync)
				{
					if (_waveIn != IntPtr.Zero)
					{
						WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
					}
					return;
				}
			}
			if (!_transmitMicrophone || (_call1Held && !_secondaryRunning) || (_call1Held && _call2Held))
			{
				lock (_buffersSync)
				{
					if (_waveIn != IntPtr.Zero)
					{
						WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
					}
					return;
				}
			}
			int num = Math.Min(waveHeader.BytesRecorded, _rawPcmBuffer.Length);
			Marshal.Copy(waveHeader.Data, _rawPcmBuffer, 0, num);
			int num2 = num / 2;
			float num3 = 0f;
			for (int i = 0; i < num2; i++)
			{
				short sample = BitConverter.ToInt16(_rawPcmBuffer, i * 2);
				short num4 = (short)((!_muted) ? ApplyGain(sample, _inputGain) : 0);
				_micSampleBuffer[i] = num4;
				float num5 = (float)Math.Abs(num4) / 32768f;
				if (num5 > num3)
				{
					num3 = num5;
				}
			}
			CurrentMicLevel = num3;
			short[] array = (_call1Held ? AudioFrameQueue.GetSilenceFrame(num2) : _call1RxQueue.DequeueOrSilence(num2));
			short[] array2 = ((!_secondaryRunning || _call2Held) ? AudioFrameQueue.GetSilenceFrame(num2) : _call2RxQueue.DequeueOrSilence(num2));
			if (IsRecording)
			{
				lock (_recordingSync)
				{
					if (_recordingWriter != null)
					{
						for (int j = 0; j < num2; j++)
						{
							short value = _micSampleBuffer[j];
							short value2 = (short)Math.Clamp(array[j] + array2[j], -32768, 32767);
							_recordingWriter.Write(value);
							_recordingWriter.Write(value2);
							_recordedDataBytes += 4;
						}
					}
				}
			}
			int length = num2 * 2;
			for (int k = 0; k < num2; k++)
			{
				short sample2 = (short)Math.Clamp(array[k] + array2[k], -32768, 32767);
				sample2 = ApplyGain(sample2, _outputGain);
				_speakerPcmBuffer[k * 2] = (byte)(sample2 & 0xFF);
				_speakerPcmBuffer[k * 2 + 1] = (byte)((sample2 >> 8) & 0xFF);
			}
			PlayPcmBuffer(_speakerPcmBuffer, length);
			if (_running && !_call1Held && _remoteEndPoint != null)
			{
				for (int l = 0; l < num2; l++)
				{
					short sample3 = (_conferenceMerged ? ((short)Math.Clamp(_micSampleBuffer[l] + array2[l], -32768, 32767)) : _micSampleBuffer[l]);
					_payload1Buffer[l] = ((_payloadType == 8) ? G711Codec.LinearToALaw(sample3) : G711Codec.LinearToMuLaw(sample3));
				}
				lock (_sendSync)
				{
					byte[] array3 = BuildRtpPacketSegment(_payload1Buffer, num2, _payloadType, _timestamp, marker: false, _ssrc, ref _sequence);
					_rtpClient.Send(array3, array3.Length, _remoteEndPoint);
					_timestamp += (uint)num2;
					Interlocked.Increment(ref _sentPackets);
				}
			}
			if (_secondaryRunning && !_call2Held && _secondaryRemoteEndPoint != null && _secondaryRtpClient != null)
			{
				for (int m = 0; m < num2; m++)
				{
					short sample4 = (_conferenceMerged ? ((short)Math.Clamp(_micSampleBuffer[m] + array[m], -32768, 32767)) : _micSampleBuffer[m]);
					_payload2Buffer[m] = ((_secondaryPayloadType == 8) ? G711Codec.LinearToALaw(sample4) : G711Codec.LinearToMuLaw(sample4));
				}
				lock (_secondarySendSync)
				{
					byte[] array4 = BuildRtpPacketSegment(_payload2Buffer, num2, _secondaryPayloadType, _secondaryTimestamp, marker: false, _secondarySsrc, ref _secondarySequence);
					_secondaryRtpClient.Send(array4, array4.Length, _secondaryRemoteEndPoint);
					_secondaryTimestamp += (uint)num2;
				}
			}
			lock (_buffersSync)
			{
				if (_waveIn != IntPtr.Zero)
				{
					WinMm.waveInAddBuffer(_waveIn, headerPointer, Marshal.SizeOf<WaveHeader>());
				}
			}
		}
		catch (Exception ex)
		{
			DebugLog.Write("WaveInCallback crash prevented: " + ex.Message);
		}
	}

	private void SendDtmfPacket(byte eventCode, ushort duration, uint timestamp, bool end, bool marker)
	{
		if (_remoteEndPoint == null)
		{
			return;
		}
		byte[] array = new byte[4]
		{
			eventCode,
			(byte)((end ? 128 : 0) | 0xA),
			0,
			0
		};
		WriteUInt16(array, 2, duration);
		lock (_sendSync)
		{
			byte[] array2 = BuildRtpPacket(array, 101, timestamp, marker, _ssrc, ref _sequence);
			_rtpClient.Send(array2, array2.Length, _remoteEndPoint);
		}
	}

	private static byte[] BuildRtpPacketSegment(byte[] payload, int length, int payloadType, uint timestamp, bool marker, uint ssrc, ref ushort sequence)
	{
		byte[] array = new byte[12 + length];
		array[0] = 128;
		array[1] = (byte)((marker ? 128 : 0) | (payloadType & 0x7F));
		WriteUInt16(array, 2, sequence++);
		WriteUInt32(array, 4, timestamp);
		WriteUInt32(array, 8, ssrc);
		Buffer.BlockCopy(payload, 0, array, 12, length);
		return array;
	}

	private static byte[] BuildRtpPacket(byte[] payload, int payloadType, uint timestamp, bool marker, uint ssrc, ref ushort sequence)
	{
		return BuildRtpPacketSegment(payload, payload.Length, payloadType, timestamp, marker, ssrc, ref sequence);
	}

	private async Task ReceiveLoopAsync(UdpClient client, AudioFrameQueue rxQueue, CancellationToken cancellationToken, bool isSecondary)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				UdpReceiveResult udpReceiveResult = await client.ReceiveAsync(cancellationToken);
				if (udpReceiveResult.Buffer.Length <= 12)
				{
					continue;
				}
				Interlocked.Increment(ref _receivedPackets);
				ushort num = (ushort)((udpReceiveResult.Buffer[2] << 8) | udpReceiveResult.Buffer[3]);
				if (_lastRxSequence > 0)
				{
					ushort num2 = (ushort)(num - _lastRxSequence);
					if (num2 > 0 && num2 < 100)
					{
						Interlocked.Add(ref _expectedPackets, num2);
					}
					else
					{
						Interlocked.Increment(ref _expectedPackets);
					}
				}
				else
				{
					Interlocked.Increment(ref _expectedPackets);
				}
				_lastRxSequence = num;
				if (!isSecondary && Volatile.Read(in _receivedPackets) == 0)
				{
					IPEndPoint remoteEndPoint = udpReceiveResult.RemoteEndPoint;
					if (remoteEndPoint != null && (_remoteEndPoint == null || !remoteEndPoint.Equals(_remoteEndPoint)))
					{
						_remoteEndPoint = remoteEndPoint;
					}
				}
				int num3 = udpReceiveResult.Buffer[1] & 0x7F;
				if ((num3 != 0 && num3 != 8) || 1 == 0)
				{
					continue;
				}
				int num4 = 12 + (udpReceiveResult.Buffer[0] & 0xF) * 4;
				if (udpReceiveResult.Buffer.Length > num4)
				{
					byte[] subArray = udpReceiveResult.Buffer[num4..];
					short[] array = new short[subArray.Length];
					for (int i = 0; i < subArray.Length; i++)
					{
						array[i] = ((num3 == 8) ? G711Codec.ALawToLinear(subArray[i]) : G711Codec.MuLawToLinear(subArray[i]));
					}
					rxQueue.Enqueue(array);
					if (!isSecondary)
					{
						Interlocked.Increment(ref _receivedPackets);
					}
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex2)
			{
				DebugLog.Write($"RTP receive error (secondary={isSecondary}): {ex2.Message}");
			}
		}
	}

	private static async Task<IPAddress> ResolveRemoteAddressAsync(string remoteAddress)
	{
		if (IPAddress.TryParse(remoteAddress, out IPAddress address))
		{
			return address;
		}
		using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5L));
		try
		{
			IPAddress[] source = await Dns.GetHostAddressesAsync(remoteAddress, cts.Token);
			return source.FirstOrDefault((IPAddress item) => item.AddressFamily == AddressFamily.InterNetwork) ?? source.FirstOrDefault() ?? throw new InvalidOperationException("Unable to resolve RTP address " + remoteAddress + ".");
		}
		catch (Exception ex)
		{
			throw new InvalidOperationException("Unable to resolve RTP address " + remoteAddress + ": " + ex.Message);
		}
	}

	private void PlayPcm(byte[] pcm)
	{
		PlayPcmBuffer(pcm, pcm.Length);
	}

	private void PlayPcmBuffer(byte[] pcm, int length)
	{
		lock (_buffersSync)
		{
			if (_waveOut == IntPtr.Zero)
			{
				try
				{
					OpenWaveOut();
				}
				catch (Exception ex)
				{
					DebugLog.Write("RTP lazy OpenWaveOut failed: " + ex.Message);
					return;
				}
			}
			AudioBuffer audioBuffer = new AudioBuffer(length);
			Marshal.Copy(pcm, 0, audioBuffer.DataPointer, length);
			_outputBuffers.Add(audioBuffer);
			WinMm.waveOutPrepareHeader(_waveOut, audioBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
			WinMm.waveOutWrite(_waveOut, audioBuffer.HeaderPointer, Marshal.SizeOf<WaveHeader>());
			if (_outputBuffers.Count > 120)
			{
				AudioBuffer audioBuffer2 = _outputBuffers[0];
				_outputBuffers.RemoveAt(0);
				WinMm.waveOutUnprepareHeader(_waveOut, audioBuffer2.HeaderPointer, Marshal.SizeOf<WaveHeader>());
				audioBuffer2.Dispose();
			}
		}
	}

	private static int ParseDeviceId(string id)
	{
		if (!int.TryParse(id, out var result))
		{
			return -1;
		}
		return result;
	}

	private static short ApplyGain(short sample, double gain)
	{
		return (short)Math.Clamp((double)sample * gain, -32768.0, 32767.0);
	}

	private static byte GetDtmfEventCode(char digit)
	{
		switch (digit)
		{
		case '0':
		case '1':
		case '2':
		case '3':
		case '4':
		case '5':
		case '6':
		case '7':
		case '8':
		case '9':
			return (byte)(digit - 48);
		case '*':
			return 10;
		case '#':
			return 11;
		case 'A':
		case 'a':
			return 12;
		case 'B':
		case 'b':
			return 13;
		case 'C':
		case 'c':
			return 14;
		case 'D':
		case 'd':
			return 15;
		default:
			throw new ArgumentOutOfRangeException("digit", "Unsupported DTMF digit.");
		}
	}

	private static void WriteUInt16(byte[] target, int offset, ushort value)
	{
		target[offset] = (byte)(value >> 8);
		target[offset + 1] = (byte)value;
	}

	private static void WriteUInt32(byte[] target, int offset, uint value)
	{
		target[offset] = (byte)(value >> 24);
		target[offset + 1] = (byte)(value >> 16);
		target[offset + 2] = (byte)(value >> 8);
		target[offset + 3] = (byte)value;
	}
}
