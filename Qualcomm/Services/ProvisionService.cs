using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace V1Per.Qualcomm.Services
{
    public class UfsGlobalConfig
    {
        public bool BootEnable { get; set; } = true;
        public int WriteProtect { get; set; }
        public int BootLun { get; set; } = 1;
        public bool WriteBoosterPreserveUserSpace { get; set; } = true;
        public long WriteBoosterBufferSize { get; set; }
    }

    public class UfsLunConfig
    {
        public int LunNumber { get; set; }
        public bool Bootable { get; set; }
        public long SizeInSectors { get; set; }
        public long SizeInKB { get; set; }
        public int SectorSize { get; set; } = 4096;
        public int MemoryType { get; set; }
        public int ProvisioningType { get; set; }
        public int DataReliability { get; set; }
        public int LogicalBlockSize { get; set; } = 4096;
    }

    public class EmmcConfig
    {
        public int BootPartition1Size { get; set; }
        public int BootPartition2Size { get; set; }
        public int RpmbSize { get; set; }
        public long[] GpPartitionSizes { get; set; } = new long[4];
        public long EnhancedUserAreaSize { get; set; }
        public long EnhancedUserAreaStart { get; set; }
    }

    public class ProvisionConfig
    {
        public string StorageType { get; set; } = "UFS";
        public UfsGlobalConfig UfsGlobal { get; set; } = new();
        public List<UfsLunConfig> UfsLuns { get; set; } = [];
        public EmmcConfig Emmc { get; set; } = new();
    }

    public class ProvisionService
    {
        private readonly Action<string> _log;

        public ProvisionService(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public ProvisionConfig ParseProvisionXml(string xmlPath)
        {
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("Provision XML file not found", xmlPath);
            return ParseProvisionXmlContent(File.ReadAllText(xmlPath));
        }

        public ProvisionConfig ParseProvisionXmlContent(string xmlContent)
        {
            var config = new ProvisionConfig();
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;
                if (root == null || root.Name.LocalName != "data") return config;

                var ufsConfig = root.Element("ufs");
                if (ufsConfig != null)
                {
                    config.StorageType = "UFS";
                    ParseUfsConfig(ufsConfig, config);
                }

                var emmcConfig = root.Element("emmc");
                if (emmcConfig != null)
                {
                    config.StorageType = "eMMC";
                    ParseEmmcConfig(emmcConfig, config);
                }

                _log($"[Provision] Parse complete: {config.StorageType}, {config.UfsLuns.Count} LUNs");
            }
            catch (Exception ex)
            {
                _log($"[Provision] Parse failed: {ex.Message}");
            }
            return config;
        }

        private void ParseUfsConfig(XElement ufsElement, ProvisionConfig config)
        {
            var global = ufsElement.Element("global");
            if (global != null)
            {
                config.UfsGlobal.BootEnable = GetBoolAttribute(global, "bBootEnable", true);
                config.UfsGlobal.WriteProtect = GetIntAttribute(global, "bSecureWriteProtectEn", 0);
                config.UfsGlobal.BootLun = GetIntAttribute(global, "bDescrAccessEn", 1);
                config.UfsGlobal.WriteBoosterPreserveUserSpace = GetBoolAttribute(global, "qWriteBoosterBufferPreserveUserSpaceEn", true);
                config.UfsGlobal.WriteBoosterBufferSize = GetLongAttribute(global, "dNumSharedWriteBoosterBufferAllocUnits", 0);
            }

            foreach (var lunElement in ufsElement.Elements("lun"))
            {
                config.UfsLuns.Add(new UfsLunConfig
                {
                    LunNumber = GetIntAttribute(lunElement, "physical_partition_number", 0),
                    Bootable = GetBoolAttribute(lunElement, "bBootLunID", false),
                    SizeInSectors = GetLongAttribute(lunElement, "num_partition_sectors", 0),
                    SizeInKB = GetLongAttribute(lunElement, "size_in_KB", 0),
                    SectorSize = GetIntAttribute(lunElement, "SECTOR_SIZE_IN_BYTES", 4096),
                    MemoryType = GetIntAttribute(lunElement, "bMemoryType", 0),
                    ProvisioningType = GetIntAttribute(lunElement, "bProvisioningType", 0),
                    DataReliability = GetIntAttribute(lunElement, "bDataReliability", 0),
                    LogicalBlockSize = GetIntAttribute(lunElement, "bLogicalBlockSize", 4096)
                });
            }
        }

        private void ParseEmmcConfig(XElement emmcElement, ProvisionConfig config)
        {
            config.Emmc.BootPartition1Size = GetIntAttribute(emmcElement, "BOOT_SIZE_MULTI1", 0);
            config.Emmc.BootPartition2Size = GetIntAttribute(emmcElement, "BOOT_SIZE_MULTI2", 0);
            config.Emmc.RpmbSize = GetIntAttribute(emmcElement, "RPMB_SIZE_MULT", 0);
            config.Emmc.EnhancedUserAreaSize = GetLongAttribute(emmcElement, "ENH_SIZE_MULT", 0);
            config.Emmc.EnhancedUserAreaStart = GetLongAttribute(emmcElement, "ENH_START_ADDR", 0);

            for (int i = 0; i < 4; i++)
                config.Emmc.GpPartitionSizes[i] = GetLongAttribute(emmcElement, $"GP_SIZE_MULT{i + 1}", 0);
        }

        public string GenerateUfsProvisionXml(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<data>");
            sb.AppendLine("  <!--NOTE: This is an ** Alarm Auto Generated file ** and target specific-->");
            sb.AppendLine();
            sb.AppendLine("  <ufs>");
            sb.AppendFormat("    <global bBootEnable=\"{0}\" bDescrAccessEn=\"{1}\" bInitPowerMode=\"1\" bInitActiveICCLevel=\"0\" bSecureRemovalType=\"0\" bConfigDescrLock=\"0\" bSecureWriteProtectEn=\"{2}\" qWriteBoosterBufferPreserveUserSpaceEn=\"{3}\" dNumSharedWriteBoosterBufferAllocUnits=\"{4}\" />\n",
                config.UfsGlobal.BootEnable ? 1 : 0, config.UfsGlobal.BootLun, config.UfsGlobal.WriteProtect,
                config.UfsGlobal.WriteBoosterPreserveUserSpace ? 1 : 0, config.UfsGlobal.WriteBoosterBufferSize);
            sb.AppendLine();

            foreach (var lun in config.UfsLuns)
            {
                sb.AppendFormat("    <lun physical_partition_number=\"{0}\" bBootLunID=\"{1}\" num_partition_sectors=\"{2}\" size_in_KB=\"{3}\" SECTOR_SIZE_IN_BYTES=\"{4}\" bMemoryType=\"{5}\" bProvisioningType=\"{6}\" bDataReliability=\"{7}\" bLogicalBlockSize=\"{8}\" />\n",
                    lun.LunNumber, lun.Bootable ? 1 : 0, lun.SizeInSectors, lun.SizeInKB, lun.SectorSize,
                    lun.MemoryType, lun.ProvisioningType, lun.DataReliability, lun.LogicalBlockSize);
            }

            sb.AppendLine("  </ufs>");
            sb.AppendLine("</data>");
            return sb.ToString();
        }

        public string GenerateEmmcProvisionXml(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" ?>");
            sb.AppendLine("<data>");
            sb.AppendFormat("  <emmc BOOT_SIZE_MULTI1=\"{0}\" BOOT_SIZE_MULTI2=\"{1}\" RPMB_SIZE_MULT=\"{2}\" ENH_SIZE_MULT=\"{3}\" ENH_START_ADDR=\"{4}\" GP_SIZE_MULT1=\"{5}\" GP_SIZE_MULT2=\"{6}\" GP_SIZE_MULT3=\"{7}\" GP_SIZE_MULT4=\"{8}\" />\n",
                config.Emmc.BootPartition1Size, config.Emmc.BootPartition2Size, config.Emmc.RpmbSize,
                config.Emmc.EnhancedUserAreaSize, config.Emmc.EnhancedUserAreaStart,
                config.Emmc.GpPartitionSizes[0], config.Emmc.GpPartitionSizes[1],
                config.Emmc.GpPartitionSizes[2], config.Emmc.GpPartitionSizes[3]);
            sb.AppendLine("</data>");
            return sb.ToString();
        }

        public void SaveProvisionXml(ProvisionConfig config, string outputPath)
        {
            string content = config.StorageType == "UFS" ? GenerateUfsProvisionXml(config) : GenerateEmmcProvisionXml(config);
            File.WriteAllText(outputPath, content, Encoding.UTF8);
            _log($"[Provision] Saved: {outputPath}");
        }

        public static ProvisionConfig CreateDefaultUfsConfig(long totalSizeGB = 256)
        {
            var config = new ProvisionConfig
            {
                StorageType = "UFS",
                UfsGlobal = new UfsGlobalConfig { BootEnable = true, BootLun = 1, WriteProtect = 0, WriteBoosterPreserveUserSpace = true, WriteBoosterBufferSize = 0x200000 }
            };

            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 0, Bootable = true, SizeInKB = 8192, MemoryType = 3 });
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 1, Bootable = true, SizeInKB = 8192, MemoryType = 3 });
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 2, Bootable = false, SizeInKB = 4096, MemoryType = 0 });
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 3, Bootable = false, SizeInKB = 512, MemoryType = 0 });
            config.UfsLuns.Add(new UfsLunConfig { LunNumber = 4, Bootable = false, SizeInKB = totalSizeGB * 1024 * 1024 - 30000, MemoryType = 0 });
            for (int i = 5; i <= 7; i++)
                config.UfsLuns.Add(new UfsLunConfig { LunNumber = i, Bootable = false, SizeInKB = 0, MemoryType = 0 });

            return config;
        }

        public static ProvisionConfig CreateDefaultEmmcConfig() => new()
        {
            StorageType = "eMMC",
            Emmc = new EmmcConfig { BootPartition1Size = 32, BootPartition2Size = 32, RpmbSize = 8 }
        };

        public string GenerateAnalysisReport(ProvisionConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("  Provision Config Analysis Report");
            sb.AppendLine("========================================");
            sb.AppendLine();
            sb.AppendLine($"Storage Type: {config.StorageType}");
            sb.AppendLine();

            if (config.StorageType == "UFS")
            {
                sb.AppendLine("[Global Config]");
                sb.AppendLine($"  Boot Enable: {(config.UfsGlobal.BootEnable ? "Yes" : "No")}");
                sb.AppendLine($"  Boot LUN: {config.UfsGlobal.BootLun}");
                sb.AppendLine($"  Write Protect: {GetWriteProtectDescription(config.UfsGlobal.WriteProtect)}");
                sb.AppendLine($"  Write Booster: {(config.UfsGlobal.WriteBoosterBufferSize > 0 ? $"{config.UfsGlobal.WriteBoosterBufferSize * 4096.0 / 1024 / 1024 / 1024:F2} GB" : "Not configured")}");
                sb.AppendLine();
                sb.AppendLine("[LUN Config]");

                long totalSize = 0;
                foreach (var lun in config.UfsLuns.OrderBy(l => l.LunNumber))
                {
                    string sizeStr = lun.SizeInKB >= 1024 * 1024 ? $"{lun.SizeInKB / 1024.0 / 1024.0:F2} GB"
                        : lun.SizeInKB >= 1024 ? $"{lun.SizeInKB / 1024.0:F2} MB"
                        : $"{lun.SizeInKB} KB";
                    sb.AppendLine($"  LUN {lun.LunNumber}: {sizeStr,-12} {(lun.Bootable ? "[BOOT]" : "")} {GetMemoryTypeDescription(lun.MemoryType)}");
                    totalSize += lun.SizeInKB;
                }
                sb.AppendLine($"\n  Total Capacity: {totalSize / 1024.0 / 1024.0:F2} GB");
            }
            else
            {
                sb.AppendLine("[eMMC Config]");
                sb.AppendLine($"  Boot Partition 1: {config.Emmc.BootPartition1Size * 128 / 1024} MB");
                sb.AppendLine($"  Boot Partition 2: {config.Emmc.BootPartition2Size * 128 / 1024} MB");
                sb.AppendLine($"  RPMB: {config.Emmc.RpmbSize * 128 / 1024} MB");
            }

            sb.AppendLine("\n========================================");
            return sb.ToString();
        }

        private static string GetWriteProtectDescription(int wp) => wp switch
        {
            0 => "No protection",
            1 => "Power on write protect",
            2 => "Permanent write protect",
            _ => $"Unknown ({wp})"
        };

        private static string GetMemoryTypeDescription(int memType) => memType switch
        {
            0 => "Normal",
            1 => "System Code",
            2 => "Non-Persistent",
            3 => "Enhanced (SLC)",
            _ => $"Unknown ({memType})"
        };

        private static int GetIntAttribute(XElement element, string name, int defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            string value = attr.Value;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                if (int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out int hexResult)) return hexResult;
            if (int.TryParse(value, out int result)) return result;
            return defaultValue;
        }

        private static long GetLongAttribute(XElement element, string name, long defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            string value = attr.Value;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                if (long.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out long hexResult)) return hexResult;
            if (long.TryParse(value, out long result)) return result;
            return defaultValue;
        }

        private static bool GetBoolAttribute(XElement element, string name, bool defaultValue)
        {
            var attr = element.Attribute(name);
            if (attr == null) return defaultValue;
            string value = attr.Value.ToLowerInvariant();
            if (value is "1" or "true" or "yes") return true;
            if (value is "0" or "false" or "no") return false;
            return defaultValue;
        }
    }
}
