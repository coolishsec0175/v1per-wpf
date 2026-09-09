using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace V1Per.Qualcomm.Services
{
    public enum StorageType
    {
        Unknown,
        EMMC,
        UFS,
        NAND
    }

    public class StorageInfo
    {
        public StorageType Type { get; set; } = StorageType.Unknown;
        public string DeviceName { get; set; } = "";
        public long TotalSizeBytes { get; set; }
        public int SectorSize { get; set; }
        public int LunsCount { get; set; }
        public string ModelName { get; set; } = "";
        public string FirmwareRevision { get; set; } = "";
        public bool IsBootable { get; set; } = true;
        public bool HasWriteBooster { get; set; }
        public List<StorageLun> Luns { get; set; } = new();
    }

    public class StorageLun
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public long SizeBytes { get; set; }
        public bool IsBootable { get; set; }
        public bool IsReadOnly { get; set; }
    }

    public static class StorageDetectService
    {
        public static StorageInfo DetectFromFirehoseResponse(string xmlResponse)
        {
            var info = new StorageInfo();

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlResponse);

                var storageNode = doc.SelectSingleNode("//storage_info");
                if (storageNode == null) return info;

                var memType = storageNode.Attributes?["MemoryName"]?.Value
                              ?? storageNode.SelectSingleNode("MemoryName")?.InnerText;
                if (!string.IsNullOrEmpty(memType))
                {
                    info.Type = memType.ToLowerInvariant() switch
                    {
                        "ufs" => StorageType.UFS,
                        "emmc" => StorageType.EMMC,
                        "nand" => StorageType.NAND,
                        _ => StorageType.Unknown
                    };
                }

                var totalSectors = storageNode.Attributes?["total_sectors"]?.Value
                                   ?? storageNode.SelectSingleNode("total_sectors")?.InnerText;
                if (long.TryParse(totalSectors, out long ts))
                    info.TotalSizeBytes = ts * (info.Type == StorageType.UFS ? 4096 : 512);

                var sectorSize = storageNode.Attributes?["sector_size"]?.Value
                                 ?? storageNode.SelectSingleNode("sector_size")?.InnerText;
                if (int.TryParse(sectorSize, out int ss))
                    info.SectorSize = ss;
                else
                    info.SectorSize = info.Type == StorageType.UFS ? 4096 : 512;

                var lunNodes = doc.SelectNodes("//storage_info/physical_partition_number");
                if (lunNodes != null)
                    info.LunsCount = lunNodes.Count;

                var modelNode = storageNode.SelectSingleNode("product_name");
                if (modelNode != null) info.ModelName = modelNode.InnerText.Trim();

                var fwNode = storageNode.SelectSingleNode("product_revision");
                if (fwNode != null) info.FirmwareRevision = fwNode.InnerText.Trim();
            }
            catch { }

            return info;
        }

        public static StorageInfo DetectFromPeekResponse(string peekResponse)
        {
            var info = new StorageInfo();
            if (string.IsNullOrEmpty(peekResponse)) return info;

            if (peekResponse.Contains("UFS", StringComparison.OrdinalIgnoreCase))
                info.Type = StorageType.UFS;
            else if (peekResponse.Contains("EMMC", StringComparison.OrdinalIgnoreCase))
                info.Type = StorageType.EMMC;

            return info;
        }

        public static StorageInfo DetectFromDeviceXml(string xmlPath)
        {
            var info = new StorageInfo();
            if (!System.IO.File.Exists(xmlPath)) return info;

            try
            {
                var doc = new XmlDocument();
                doc.Load(xmlPath);

                var memNode = doc.SelectSingleNode("//MemoryName");
                if (memNode != null)
                {
                    info.Type = memNode.InnerText.ToLowerInvariant() switch
                    {
                        "ufs" => StorageType.UFS,
                        "emmc" => StorageType.EMMC,
                        "nand" => StorageType.NAND,
                        _ => StorageType.Unknown
                    };
                }

                var totalNode = doc.SelectSingleNode("//total_sectors");
                if (totalNode != null && long.TryParse(totalNode.InnerText, out long ts))
                    info.TotalSizeBytes = ts * (info.Type == StorageType.UFS ? 4096 : 512);

                info.SectorSize = info.Type == StorageType.UFS ? 4096 : 512;
            }
            catch { }

            return info;
        }

        public static bool IsUfs(StorageInfo info) => info.Type == StorageType.UFS;
        public static bool IsEmmc(StorageInfo info) => info.Type == StorageType.EMMC;

        public static int GetSectorSize(StorageInfo info) =>
            info.Type == StorageType.UFS ? 4096 : 512;

        public static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:F2} {units[unitIndex]}";
        }
    }
}
