using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace V1Per.Qualcomm.Services
{
    public class GptEntry
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public Guid TypeGuid { get; set; }
        public Guid UniqueGuid { get; set; }
        public long StartLba { get; set; }
        public long EndLba { get; set; }
        public long SizeSectors { get; set; }
        public string SizeHuman { get; set; } = "";
        public bool IsRequired { get; set; }
    }

    public class GptHeader
    {
        public string Signature { get; set; } = "";
        public uint Revision { get; set; }
        public int HeaderSize { get; set; }
        public uint HeaderCrc32 { get; set; }
        public long MyLba { get; set; }
        public long AlternateLba { get; set; }
        public long FirstUsableLba { get; set; }
        public long LastUsableLba { get; set; }
        public Guid DiskGuid { get; set; }
        public long PartitionEntryLba { get; set; }
        public int PartitionEntryCount { get; set; }
        public int PartitionEntrySize { get; set; }
    }

    public class GptBackupResult
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; } = "";
        public int PartitionCount { get; set; }
        public string Message { get; set; } = "";
    }

    public class GptService
    {
        private readonly Action<string> _log;

        private static readonly Guid partitionTypeBasic = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid partitionTypeLinux = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid partitionTypeBoot = new("21686148-6449-6E6F-744E-656564454649");
        private static readonly Guid partitionTypeSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");

        private static readonly Dictionary<Guid, string> knownTypes = new()
        {
            [new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "EFI System",
            [partitionTypeBasic] = "Microsoft Basic",
            [partitionTypeLinux] = "Linux Filesystem",
            [new("024DEE41-33E7-11D3-9D69-0008C781F39F")] = "MBR Scheme",
            [partitionTypeBoot] = "BIOS Boot",
            [partitionTypeSwap] = "Linux Swap",
            [new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = "Microsoft Reserved",
            [new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = "Windows Recovery",
        };

        private static readonly Dictionary<string, Guid> nameToType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["system"] = partitionTypeBasic,
            ["vendor"] = partitionTypeBasic,
            ["userdata"] = partitionTypeBasic,
            ["product"] = partitionTypeBasic,
            ["system_ext"] = partitionTypeBasic,
            ["odm"] = partitionTypeBasic,
            ["cache"] = partitionTypeLinux,
            ["meta"] = partitionTypeLinux,
            ["misc"] = partitionTypeLinux,
            ["recovery"] = partitionTypeLinux,
            ["boot"] = partitionTypeBoot,
            ["vbmeta"] = partitionTypeLinux,
            ["vbmeta_system"] = partitionTypeLinux,
            ["modem"] = new("21686148-6449-6E6F-744E-656564454649"),
        };

        public GptService(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public GptBackupResult BackupGpt(byte[] deviceGptData, long diskSizeSectors, string outputDir)
        {
            var result = new GptBackupResult();
            try
            {
                if (deviceGptData == null || deviceGptData.Length < 512)
                {
                    result.Message = "Invalid GPT data";
                    return result;
                }

                var header = ParseGptHeader(deviceGptData);
                if (header == null)
                {
                    result.Message = "Invalid GPT header signature";
                    return result;
                }

                var entries = ParsePartitionEntries(deviceGptData, header);
                result.PartitionCount = entries.Count;
                result.Success = true;

                if (string.IsNullOrEmpty(outputDir))
                    outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "V1Per", "GPT_Backup");
                Directory.CreateDirectory(outputDir);

                var sb = new StringBuilder();
                sb.AppendLine("# V1Per GPT Backup");
                sb.AppendLine($"# Disk GUID: {header.DiskGuid}");
                sb.AppendLine($"# Total Sectors: {diskSizeSectors}");
                sb.AppendLine($"# Sector Size: 512");
                sb.AppendLine($"# Partitions: {entries.Count}");
                sb.AppendLine("# Format: index|name|start_lba|end_lba|size_sectors|type_guid");
                sb.AppendLine();

                foreach (var e in entries.OrderBy(x => x.StartLba))
                {
                    sb.AppendLine($"{e.Index}|{e.Name}|{e.StartLba}|{e.EndLba}|{e.SizeSectors}|{e.TypeGuid}");
                }

                string backupFile = Path.Combine(outputDir, $"gpt_backup_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(backupFile, sb.ToString(), Encoding.UTF8);
                result.OutputPath = backupFile;

                _log($"[GPT] Backup saved: {backupFile}");
                _log($"[GPT] {entries.Count} partitions backed up");
            }
            catch (Exception ex)
            {
                result.Message = $"Backup failed: {ex.Message}";
                _log($"[GPT] {result.Message}");
            }
            return result;
        }

        public GptBackupResult BackupFromRawProgram(string rawProgramXmlPath, string outputDir)
        {
            var result = new GptBackupResult();
            try
            {
                if (!File.Exists(rawProgramXmlPath))
                {
                    result.Message = "rawprogram XML not found";
                    return result;
                }

                var doc = new System.Xml.XmlDocument();
                doc.Load(rawProgramXmlPath);
                var root = doc.SelectSingleNode("//data");
                if (root == null)
                {
                    result.Message = "Invalid rawprogram XML format";
                    return result;
                }

                var entries = new List<GptEntry>();
                int idx = 0;
                foreach (System.Xml.XmlNode node in root.ChildNodes)
                {
                    if (node.Name == "program")
                    {
                        var label = node.Attributes?["label"]?.Value ?? "";
                        var startSector = node.Attributes?["start_sector"]?.Value ?? "0";
                        var numSectors = node.Attributes?["num_partition_sectors"]?.Value ?? "0";
                        var partNum = node.Attributes?["physical_partition_number"]?.Value ?? "0";

                        if (partNum == "0" && !string.IsNullOrEmpty(label) && label != "gpt")
                        {
                            long.TryParse(startSector, out long start);
                            long.TryParse(numSectors, out long count);
                            Guid type = nameToType.TryGetValue(label, out var t) ? t : partitionTypeBasic;

                            entries.Add(new GptEntry
                            {
                                Index = idx++,
                                Name = label,
                                StartLba = start,
                                SizeSectors = count,
                                EndLba = start + count - 1,
                                TypeGuid = type,
                                SizeHuman = FormatSectors(count)
                            });
                        }
                    }
                }

                result.PartitionCount = entries.Count;
                result.Success = true;

                if (string.IsNullOrEmpty(outputDir))
                    outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "V1Per", "GPT_Backup");
                Directory.CreateDirectory(outputDir);

                var sb = new StringBuilder();
                sb.AppendLine("# V1Per GPT Backup (from rawprogram)");
                sb.AppendLine($"# Source: {Path.GetFileName(rawProgramXmlPath)}");
                sb.AppendLine($"# Partitions: {entries.Count}");
                sb.AppendLine("# Format: index|name|start_lba|end_lba|size_sectors|type_guid");
                sb.AppendLine();

                foreach (var e in entries.OrderBy(x => x.StartLba))
                    sb.AppendLine($"{e.Index}|{e.Name}|{e.StartLba}|{e.EndLba}|{e.SizeSectors}|{e.TypeGuid}");

                string backupFile = Path.Combine(outputDir, $"gpt_backup_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(backupFile, sb.ToString(), Encoding.UTF8);
                result.OutputPath = backupFile;

                _log($"[GPT] Backup saved: {backupFile}");
            }
            catch (Exception ex)
            {
                result.Message = $"Backup failed: {ex.Message}";
            }
            return result;
        }

        public List<GptEntry> ParseFromRawProgram(string rawProgramXmlPath)
        {
            var entries = new List<GptEntry>();
            if (!File.Exists(rawProgramXmlPath)) return entries;

            var doc = new System.Xml.XmlDocument();
            doc.Load(rawProgramXmlPath);
            var root = doc.SelectSingleNode("//data");
            if (root == null) return entries;

            int idx = 0;
            foreach (System.Xml.XmlNode node in root.ChildNodes)
            {
                if (node.Name != "program") continue;
                var label = node.Attributes?["label"]?.Value ?? "";
                var startSector = node.Attributes?["start_sector"]?.Value ?? "0";
                var numSectors = node.Attributes?["num_partition_sectors"]?.Value ?? "0";
                var partNum = node.Attributes?["physical_partition_number"]?.Value ?? "0";

                if (partNum != "0" || string.IsNullOrEmpty(label) || label == "gpt") continue;

                long.TryParse(startSector, out long start);
                long.TryParse(numSectors, out long count);
                Guid type = nameToType.TryGetValue(label, out var t) ? t : partitionTypeBasic;

                entries.Add(new GptEntry
                {
                    Index = idx++,
                    Name = label,
                    StartLba = start,
                    SizeSectors = count,
                    EndLba = start + count - 1,
                    TypeGuid = type,
                    SizeHuman = FormatSectors(count)
                });
            }
            return entries;
        }

        private GptHeader ParseGptHeader(byte[] data)
        {
            if (data.Length < 92) return null;
            string sig = Encoding.ASCII.GetString(data, 0, 8);
            if (sig != "EFI PART") return null;

            return new GptHeader
            {
                Signature = sig,
                Revision = BitConverter.ToUInt32(data, 8),
                HeaderSize = BitConverter.ToInt32(data, 12),
                HeaderCrc32 = BitConverter.ToUInt32(data, 16),
                MyLba = BitConverter.ToInt64(data, 24),
                AlternateLba = BitConverter.ToInt64(data, 32),
                FirstUsableLba = BitConverter.ToInt64(data, 40),
                LastUsableLba = BitConverter.ToInt64(data, 48),
                DiskGuid = new Guid(data.Skip(56).Take(16).ToArray()),
                PartitionEntryLba = BitConverter.ToInt64(data, 72),
                PartitionEntryCount = BitConverter.ToInt32(data, 80),
                PartitionEntrySize = BitConverter.ToInt32(data, 84)
            };
        }

        private List<GptEntry> ParsePartitionEntries(byte[] data, GptHeader header)
        {
            var entries = new List<GptEntry>();
            int entrySize = header.PartitionEntrySize > 0 ? header.PartitionEntrySize : 128;
            int entryCount = header.PartitionEntryCount > 0 ? header.PartitionEntryCount : 128;
            int baseOffset = 512;

            for (int i = 0; i < entryCount && baseOffset + i * entrySize + entrySize <= data.Length; i++)
            {
                int offset = baseOffset + i * entrySize;
                var typeGuid = new Guid(data.Skip(offset).Take(16).ToArray());
                if (typeGuid == Guid.Empty) continue;

                var uniqueGuid = new Guid(data.Skip(offset + 16).Take(16).ToArray());
                long startLba = BitConverter.ToInt64(data, offset + 32);
                long endLba = BitConverter.ToInt64(data, offset + 40);
                string name = Encoding.Unicode.GetString(data, offset + 56, 72).TrimEnd('\0');

                entries.Add(new GptEntry
                {
                    Index = i,
                    Name = name,
                    TypeGuid = typeGuid,
                    UniqueGuid = uniqueGuid,
                    StartLba = startLba,
                    EndLba = endLba,
                    SizeSectors = endLba - startLba + 1,
                    SizeHuman = FormatSectors(endLba - startLba + 1)
                });
            }
            return entries;
        }

        public static string FormatSectors(long sectors)
        {
            long bytes = sectors * 512;
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return $"{size:F2} {units[unit]}";
        }
    }
}
