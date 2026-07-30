using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using MerlinSip.Models;

namespace MerlinSip.Services;

public class PcapCapturer
{
	private readonly MemoryStream _outputStream;
	private readonly object _lock = new object();
	private bool _isWriting;
	private int _packetCount;
	private int _totalBytes;
	private DateTime? _startTime;
	private Socket? _rawSocket;
	private Task? _captureTask;
	private CancellationTokenSource? _captureCts;
	private string? _ipFilter;

	public string? IpFilter
	{
		get
		{
			lock (_lock)
			{
				return _ipFilter;
			}
		}
		set
		{
			lock (_lock)
			{
				_ipFilter = value;
			}
		}
	}

	public int PacketCount
	{
		get
		{
			lock (_lock)
			{
				return _packetCount;
			}
		}
	}

	public int TotalBytes
	{
		get
		{
			lock (_lock)
			{
				return _totalBytes;
			}
		}
	}

	public double DurationSeconds
	{
		get
		{
			lock (_lock)
			{
				if (!_isWriting || !_startTime.HasValue)
				{
					return 0.0;
				}
				return (DateTime.UtcNow - _startTime.Value).TotalSeconds;
			}
		}
	}

	public PcapCapturer()
	{
		_outputStream = new MemoryStream();
	}

	public void Start(bool startRawSniffer = false, string? ipFilter = null, string? adapterIp = null)
	{
		if (AppCacheService.ActiveConfig?.EnableAdvancedDiagnostics != true)
		{
			return;
		}
		Stop();
		lock (_lock)
		{
			_outputStream.SetLength(0L);
			WriteGlobalHeader();
			_packetCount = 0;
			_totalBytes = 0;
			_startTime = DateTime.UtcNow;
			_ipFilter = ipFilter;
			_isWriting = true;
			if (startRawSniffer)
			{
				StartRawSocketSniffer(adapterIp);
			}
		}
	}

	public void Stop()
	{
		CancellationTokenSource? oldCts = null;
		lock (_lock)
		{
			_isWriting = false;
			try
			{
				if (_captureCts != null)
				{
					oldCts = _captureCts;
					oldCts.Cancel();
				}
				_rawSocket?.Close();
			}
			catch
			{
			}
			_captureCts = null;
			_rawSocket = null;
			_captureTask = null;
		}
		if (oldCts == null)
		{
			return;
		}
		Task.Delay(500).ContinueWith(delegate
		{
			try
			{
				oldCts.Dispose();
			}
			catch
			{
			}
		});
	}

	public byte[] GetPcapBytes()
	{
		lock (_lock)
		{
			return _outputStream.ToArray();
		}
	}

	private void WriteGlobalHeader()
	{
		WriteUInt32(2712847316u);
		WriteUInt16(2);
		WriteUInt16(4);
		WriteInt32(0);
		WriteUInt32(0u);
		WriteUInt32(65535u);
		WriteUInt32(1u);
	}

	public void RecordPacket(byte[] payload, string srcIp, int srcPort, string destIp, int destPort, bool isUdp)
	{
		lock (_lock)
		{
			if (!_isWriting || _totalBytes > 26214400 || _packetCount > 50000)
			{
				return;
			}
			string? ipFilter = _ipFilter;
			if (!string.IsNullOrEmpty(ipFilter) && srcIp != ipFilter && destIp != ipFilter)
			{
				return;
			}
			try
			{
				IPAddress address;
				IPAddress obj = (IPAddress.TryParse(srcIp, out address) ? address : IPAddress.Loopback);
				IPAddress address2;
				IPAddress iPAddress = (IPAddress.TryParse(destIp, out address2) ? address2 : IPAddress.Loopback);
				byte[] array = new byte[14]
				{
					0, 17, 34, 51, 68, 85, 102, 119, 136, 153,
					170, 187, 8, 0
				};
				byte[] array2 = new byte[20]
				{
					69, 0, 0, 0, 0, 0, 0, 0, 0, 0,
					0, 0, 0, 0, 0, 0, 0, 0, 0, 0
				};
				int num = (isUdp ? 8 : 20);
				int num2 = 20 + num + payload.Length;
				array2[2] = (byte)((num2 >> 8) & 0xFF);
				array2[3] = (byte)(num2 & 0xFF);
				array2[4] = 0;
				array2[5] = 0;
				array2[6] = 64;
				array2[7] = 0;
				array2[8] = 64;
				array2[9] = (byte)(isUdp ? 17u : 6u);
				array2[10] = 0;
				array2[11] = 0;
				Array.Copy(obj.GetAddressBytes(), 0, array2, 12, 4);
				Array.Copy(iPAddress.GetAddressBytes(), 0, array2, 16, 4);
				ushort num3 = ComputeIpChecksum(array2);
				array2[10] = (byte)((num3 >> 8) & 0xFF);
				array2[11] = (byte)(num3 & 0xFF);
				byte[] array3 = new byte[num];
				array3[0] = (byte)((srcPort >> 8) & 0xFF);
				array3[1] = (byte)(srcPort & 0xFF);
				array3[2] = (byte)((destPort >> 8) & 0xFF);
				array3[3] = (byte)(destPort & 0xFF);
				if (isUdp)
				{
					int num4 = 8 + payload.Length;
					array3[4] = (byte)((num4 >> 8) & 0xFF);
					array3[5] = (byte)(num4 & 0xFF);
					array3[6] = 0;
					array3[7] = 0;
				}
				else
				{
					array3[4] = 0;
					array3[5] = 0;
					array3[6] = 0;
					array3[7] = 1;
					array3[8] = 0;
					array3[9] = 0;
					array3[10] = 0;
					array3[11] = 1;
					array3[12] = 80;
					array3[13] = 24;
					array3[14] = 250;
					array3[15] = 240;
					array3[16] = 0;
					array3[17] = 0;
					array3[18] = 0;
					array3[19] = 0;
				}
				long num5 = DateTime.UtcNow.Ticks / 10;
				long num6 = num5 / 1000000;
				long num7 = num5 % 1000000;
				int num8 = array.Length + array2.Length + array3.Length + payload.Length;
				WriteUInt32((uint)num6);
				WriteUInt32((uint)num7);
				WriteUInt32((uint)num8);
				WriteUInt32((uint)num8);
				_outputStream.Write(array, 0, array.Length);
				_outputStream.Write(array2, 0, array2.Length);
				_outputStream.Write(array3, 0, array3.Length);
				_outputStream.Write(payload, 0, payload.Length);
				_packetCount++;
				_totalBytes += 16 + num8;
			}
			catch
			{
			}
		}
	}

	private ushort ComputeIpChecksum(byte[] header)
	{
		uint num = 0u;
		for (int i = 0; i < header.Length; i += 2)
		{
			ushort num2 = (ushort)((header[i] << 8) + header[i + 1]);
			num += num2;
		}
		while (num >> 16 != 0)
		{
			num = (num & 0xFFFF) + (num >> 16);
		}
		return (ushort)(~num);
	}

	private void WriteUInt32(uint val)
	{
		byte[] bytes = BitConverter.GetBytes(val);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(bytes);
		}
		_outputStream.Write(bytes, 0, 4);
	}

	private void WriteInt32(int val)
	{
		byte[] bytes = BitConverter.GetBytes(val);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(bytes);
		}
		_outputStream.Write(bytes, 0, 4);
	}

	private void WriteUInt16(ushort val)
	{
		byte[] bytes = BitConverter.GetBytes(val);
		if (!BitConverter.IsLittleEndian)
		{
			Array.Reverse(bytes);
		}
		_outputStream.Write(bytes, 0, 2);
	}

	private void StartRawSocketSniffer(string? adapterIp = null)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
				{
					WindowsPrincipal principal = new WindowsPrincipal(identity);
					if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
					{
						DebugLog.Write("Warning: PcapCapturer requires Administrator privileges. Packet sniffing disabled.");
						return;
					}
				}
			}

			string text = ((!string.IsNullOrEmpty(adapterIp)) ? adapterIp : GetLocalIpAddress());
			if (text == "127.0.0.1" || string.IsNullOrEmpty(text))
			{
				throw new InvalidOperationException("No active local IP address found to bind packet sniffer.");
			}
			_rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
			_rawSocket.Bind(new IPEndPoint(IPAddress.Parse(text), 0));
			byte[] optionInValue = new byte[4] { 1, 0, 0, 0 };
			byte[] optionOutValue = new byte[4];
			_rawSocket.IOControl(IOControlCode.ReceiveAll, optionInValue, optionOutValue);
			_captureCts = new CancellationTokenSource();
			CancellationToken token = _captureCts.Token;
			_captureTask = Task.Run(() => CaptureLoopAsync(token), token);
		}
		catch (SocketException ex)
		{
			if (ex.SocketErrorCode == SocketError.AccessDenied || ex.ErrorCode == 10013)
			{
				throw new UnauthorizedAccessException("Administrative privileges are required to perform raw packet capture on Windows. Please run the application as Administrator.", ex);
			}
			throw;
		}
	}

	private async Task CaptureLoopAsync(CancellationToken token)
	{
		byte[] buffer = new byte[65535];
		while (!token.IsCancellationRequested)
		{
			try
			{
				Socket? rawSocket = _rawSocket;
				if (rawSocket == null)
				{
					break;
				}
				int num = await rawSocket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token);
				if (num <= 0)
				{
					continue;
				}
				byte[] array = new byte[num];
				Array.Copy(buffer, 0, array, 0, num);
				if (array.Length >= 20)
				{
					string text = new IPAddress(new byte[4]
					{
						array[12],
						array[13],
						array[14],
						array[15]
					}).ToString();
					string text2 = new IPAddress(new byte[4]
					{
						array[16],
						array[17],
						array[18],
						array[19]
					}).ToString();
					string? ipFilter = IpFilter;
					if (string.IsNullOrEmpty(ipFilter) || !(text != ipFilter) || !(text2 != ipFilter))
					{
						RecordRawIpPacket(array);
					}
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception)
			{
				if (token.IsCancellationRequested || _rawSocket == null)
				{
					break;
				}
				await Task.Delay(10, token);
			}
		}
	}

	public void RecordRawIpPacket(byte[] ipPacket)
	{
		lock (_lock)
		{
			if (!_isWriting || _totalBytes > 26214400 || _packetCount > 50000)
			{
				return;
			}
			try
			{
				byte[] array = new byte[14]
				{
					0, 17, 34, 51, 68, 85, 102, 119, 136, 153,
					170, 187, 8, 0
				};
				int num = array.Length + ipPacket.Length;
				long num2 = DateTime.UtcNow.Ticks / 10;
				long num3 = num2 / 1000000;
				long num4 = num2 % 1000000;
				WriteUInt32((uint)num3);
				WriteUInt32((uint)num4);
				WriteUInt32((uint)num);
				WriteUInt32((uint)num);
				_outputStream.Write(array, 0, array.Length);
				_outputStream.Write(ipPacket, 0, ipPacket.Length);
				_packetCount++;
				_totalBytes += 16 + num;
			}
			catch
			{
			}
		}
	}

	private string GetLocalIpAddress()
	{
		try
		{
			using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP);
			socket.Connect("8.8.8.8", 65530);
			return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "";
		}
		catch
		{
			return "";
		}
	}
}
