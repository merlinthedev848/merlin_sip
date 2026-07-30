using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Tasks;
using MerlinSip.Models;

namespace MerlinSip.Services;

public static class HardwareChecker
{
	public static async Task<List<HardwareItem>> RunDiagnosticsAsync()
	{
		List<HardwareItem> results = new List<HardwareItem>();
		await Task.Run(() =>
		{
			CheckProcessor(results);
			CheckMemory(results);
			CheckDiskDrives(results);
			CheckBattery(results);
		});
		return results;
	}

	private static void CheckProcessor(List<HardwareItem> results)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Name, LoadPercentage, NumberOfCores, MaxClockSpeed FROM Win32_Processor");
			foreach (ManagementBaseObject item in managementObjectSearcher.Get())
			{
				string name = item["Name"]?.ToString() ?? "Unknown CPU";
				string text = item["LoadPercentage"]?.ToString() ?? "0";
				string value = item["NumberOfCores"]?.ToString() ?? "0";
				string value2 = item["MaxClockSpeed"]?.ToString() ?? "0";
				bool isHealthy = true;
				string status = "Healthy";
				if (int.TryParse(text, out var result) && result > 95)
				{
					isHealthy = false;
					status = "Critical Load";
				}
				results.Add(new HardwareItem
				{
					ComponentType = "Processor (CPU)",
					Name = name,
					Status = status,
					Details = $"Cores: {value} | Max Speed: {value2} MHz | Current Load: {text}%",
					IsHealthy = isHealthy
				});
			}
		}
		catch (Exception ex)
		{
			results.Add(new HardwareItem
			{
				ComponentType = "Processor (CPU)",
				Name = "Error",
				Status = "Error",
				Details = ex.Message,
				IsHealthy = false
			});
		}
	}

	private static void CheckMemory(List<HardwareItem> results)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory");
			long num = 0L;
			int num2 = 0;
			string text = "";
			string text2 = "";
			foreach (ManagementBaseObject item in managementObjectSearcher.Get())
			{
				if (long.TryParse(item["Capacity"]?.ToString(), out var result))
				{
					num += result;
				}
				text = item["Speed"]?.ToString() ?? text;
				text2 = item["Manufacturer"]?.ToString() ?? text2;
				num2++;
			}
			if (num2 > 0)
			{
				double value = (double)num / 1073741824.0;
				results.Add(new HardwareItem
				{
					ComponentType = "Physical Memory (RAM)",
					Name = $"{num2} Module(s) ({text2})",
					Status = "Healthy",
					Details = $"Total Capacity: {Math.Round(value, 1)} GB | Speed: {text} MHz",
					IsHealthy = true
				});
			}
		}
		catch (Exception ex)
		{
			results.Add(new HardwareItem
			{
				ComponentType = "Memory (RAM)",
				Name = "Error",
				Status = "Error",
				Details = ex.Message,
				IsHealthy = false
			});
		}
	}

	private static void CheckDiskDrives(List<HardwareItem> results)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Model, Size, Status, MediaType FROM Win32_DiskDrive");
			foreach (ManagementBaseObject item in managementObjectSearcher.Get())
			{
				string name = item["Model"]?.ToString() ?? "Unknown Drive";
				string text = item["Status"]?.ToString() ?? "Unknown";
				string value = item["MediaType"]?.ToString() ?? "Unknown Media";
				double value2 = 0.0;
				if (long.TryParse(item["Size"]?.ToString(), out var result))
				{
					value2 = (double)result / 1073741824.0;
				}
				bool flag = text.Equals("OK", StringComparison.OrdinalIgnoreCase);
				results.Add(new HardwareItem
				{
					ComponentType = "Storage (Disk)",
					Name = name,
					Status = (flag ? "Healthy (SMART OK)" : ("Warning (" + text + ")")),
					Details = $"Capacity: {Math.Round(value2, 1)} GB | Type: {value}",
					IsHealthy = flag
				});
			}
		}
		catch (Exception ex)
		{
			results.Add(new HardwareItem
			{
				ComponentType = "Storage (Disk)",
				Name = "Error",
				Status = "Error",
				Details = ex.Message,
				IsHealthy = false
			});
		}
	}

	private static void CheckBattery(List<HardwareItem> results)
	{
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Name, EstimatedChargeRemaining, DesignCapacity, FullChargeCapacity, BatteryStatus FROM Win32_Battery");
			if (managementObjectSearcher.Get().Count == 0)
			{
				return;
			}
			foreach (ManagementBaseObject item in managementObjectSearcher.Get())
			{
				string name = item["Name"]?.ToString() ?? "System Battery";
				string text = item["EstimatedChargeRemaining"]?.ToString() ?? "Unknown";
				double num = 0.0;
				double num2 = 0.0;
				if (double.TryParse(item["DesignCapacity"]?.ToString(), out var result))
				{
					num = result;
				}
				if (double.TryParse(item["FullChargeCapacity"]?.ToString(), out var result2))
				{
					num2 = result2;
				}
				string text2 = "Current Charge: " + text + "%";
				bool isHealthy = true;
				string status = "Healthy";
				if (num > 0.0 && num2 > 0.0)
				{
					double num3 = num2 / num * 100.0;
					text2 += $" | Wear Level: {Math.Round(100.0 - num3, 1)}% (Health: {Math.Round(num3, 1)}%)";
					if (num3 < 50.0)
					{
						isHealthy = false;
						status = "Degraded (Replace soon)";
					}
				}
				results.Add(new HardwareItem
				{
					ComponentType = "Battery",
					Name = name,
					Status = status,
					Details = text2,
					IsHealthy = isHealthy
				});
			}
		}
		catch
		{
		}
	}
}
