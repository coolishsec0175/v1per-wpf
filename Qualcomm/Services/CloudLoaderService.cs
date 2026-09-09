using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace V1Per.Qualcomm.Services
{
    public class LoaderMatch
    {
        public string Chipset { get; set; } = "";
        public string LoaderPath { get; set; } = "";
        public string Source { get; set; } = ""; // "local" or "cloud"
        public bool IsLite { get; set; }
        public string MemoryType { get; set; } = "ufs";
    }

    public class SaharaDeviceInfo
    {
        public string SerialPort { get; set; } = "";
        public uint Mode { get; set; }
        public string OemId { get; set; } = "";
        public string OemPkHash { get; set; } = "";
        public string SwId { get; set; } = "";
        public string HwId { get; set; } = "";
        public string ChipId { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Variant { get; set; } = "";
        public string PblSw { get; set; } = "";
        public string PblHw { get; set; } = "";
        public bool IsUfs { get; set; } = true;
        public string StorageInfo { get; set; } = "";
        public DateTime DetectedAt { get; set; } = DateTime.Now;
    }

    public class CloudLoaderService
    {
        private static readonly HttpClient _client = new();
        private static readonly string _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "V1Per", "Loaders", "Cache");
        private static readonly string _apiBase = "https://www.xiriacg.top/api";
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        static CloudLoaderService()
        {
            if (!Directory.Exists(_cacheDir))
                Directory.CreateDirectory(_cacheDir);
        }

        public static async Task<LoaderMatch> FindLoaderAsync(
            SaharaDeviceInfo deviceInfo,
            string localLoaderDir,
            bool preferLite = false)
        {
            var local = FindLocalLoader(localLoaderDir, deviceInfo, preferLite);
            if (local != null) return local;

            try
            {
                return await QueryCloudForLoader(deviceInfo, preferLite);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CloudLoader] Cloud query failed: {ex.Message}");
                return new LoaderMatch { Source = "cloud_failed", LoaderPath = "" };
            }
        }

        private static LoaderMatch? FindLocalLoader(
            string baseDir,
            SaharaDeviceInfo info,
            bool preferLite)
        {
            if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
                return null;

            var elfFiles = Directory.GetFiles(baseDir, "*.elf", SearchOption.AllDirectories);
            if (elfFiles.Length == 0) return null;

            foreach (var elf in elfFiles)
            {
                var name = Path.GetFileNameWithoutExtension(elf).ToLowerInvariant();
                if (preferLite && name.Contains("lite"))
                    return new LoaderMatch { LoaderPath = elf, Source = "local", IsLite = true };
                if (!preferLite && !name.Contains("lite"))
                    return new LoaderMatch { LoaderPath = elf, Source = "local", IsLite = false };
            }

            return new LoaderMatch { LoaderPath = elfFiles[0], Source = "local" };
        }

        private static async Task<LoaderMatch> QueryCloudForLoader(
            SaharaDeviceInfo info,
            bool preferLite)
        {
            var payload = new
            {
                action = "get_loader",
                chipset = info.ChipId,
                oem_id = info.OemId,
                sw_id = info.SwId,
                hw_id = info.HwId,
                serial = info.SerialNumber,
                mode = info.Mode.ToString(),
                prefer_lite = preferLite
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _client.PostAsync($"{_apiBase}/qualcomm/loader", content);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<CloudLoaderResponse>(body, _jsonOpts);

            if (result?.Success == true && !string.IsNullOrEmpty(result.LoaderUrl))
            {
                var localPath = await DownloadLoader(result.LoaderUrl, result.LoaderName);
                return new LoaderMatch
                {
                    LoaderPath = localPath,
                    Source = "cloud",
                    IsLite = preferLite || result.IsLite,
                    MemoryType = result.MemoryType ?? "ufs"
                };
            }

            return new LoaderMatch { Source = "cloud_empty", LoaderPath = "" };
        }

        private static async Task<string> DownloadLoader(string url, string fileName)
        {
            var localPath = Path.Combine(_cacheDir, fileName);
            if (File.Exists(localPath)) return localPath;

            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }

        public static List<string> GetCachedLoaders()
        {
            if (!Directory.Exists(_cacheDir)) return new();
            return Directory.GetFiles(_cacheDir, "*.elf").ToList();
        }

        public static void ClearCache()
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, true);
        }

        private class CloudLoaderResponse
        {
            public bool Success { get; set; }
            public string LoaderUrl { get; set; } = "";
            public string LoaderName { get; set; } = "";
            public bool IsLite { get; set; }
            public string? MemoryType { get; set; }
            public string? Error { get; set; }
        }
    }
}
