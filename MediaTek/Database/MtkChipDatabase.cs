using System;
using System.Collections.Generic;
using v1per_wpf.MediaTek.Models;

namespace v1per_wpf.MediaTek.Database
{
    public class ChipRecord
    {
        public ushort HwCode { get; set; }
        public string ChipName { get; set; } = "";
        public string Description { get; set; } = "";
        public uint WatchdogAddr { get; set; }
        public uint UartAddr { get; set; }
        public uint BromPayloadAddr { get; set; }
        public uint DaPayloadAddr { get; set; }
        public uint? CqDmaBase { get; set; }
        public int DaMode { get; set; } = 6;
        public bool SupportsXFlash { get; set; }
        public bool RequiresSignature { get; set; }
        public bool Is64Bit { get; set; }
        public string ExploitType { get; set; } = "";
        public int Da1SigLen { get; set; } = 0x1000;
        public int Da2SigLen { get; set; } = 0;
    }

    public static class MtkChipDatabase
    {
        private static readonly Dictionary<ushort, ChipRecord> _chips = new()
        {
            [0x0321] = new() { HwCode = 0x0321, ChipName = "MT6735", Description = "Cortex-A53", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0326] = new() { HwCode = 0x0326, ChipName = "MT6755", Description = "Helio P10", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0335] = new() { HwCode = 0x0335, ChipName = "MT6737", Description = "Cortex-A53", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0507] = new() { HwCode = 0x0507, ChipName = "MT6779", Description = "Helio P90", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0551] = new() { HwCode = 0x0551, ChipName = "MT6768", Description = "Helio G85", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0562] = new() { HwCode = 0x0562, ChipName = "MT6761", Description = "Helio A22", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0570] = new() { HwCode = 0x0570, ChipName = "MT6580", Description = "Cortex-A7", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0571] = new() { HwCode = 0x0571, ChipName = "MT6572", Description = "Cortex-A7 Dual", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0588] = new() { HwCode = 0x0588, ChipName = "MT6785", Description = "Helio G90T", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0600] = new() { HwCode = 0x0600, ChipName = "MT6853", Description = "Dimensity 720", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0688] = new() { HwCode = 0x0688, ChipName = "MT6771", Description = "Helio P70", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0717] = new() { HwCode = 0x0717, ChipName = "MT6765", Description = "Helio P35", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x0766] = new() { HwCode = 0x0766, ChipName = "MT6877", Description = "Dimensity 900", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0788] = new() { HwCode = 0x0788, ChipName = "MT6873", Description = "Dimensity 800", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0813] = new() { HwCode = 0x0813, ChipName = "MT6833", Description = "Dimensity 700", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0886] = new() { HwCode = 0x0886, ChipName = "MT6885", Description = "Dimensity 1000", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0989] = new() { HwCode = 0x0989, ChipName = "MT6891", Description = "Dimensity 1200", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x0996] = new() { HwCode = 0x0996, ChipName = "MT6895", Description = "Dimensity 8100", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x1209] = new() { HwCode = 0x1209, ChipName = "MT6985", Description = "Dimensity 9200", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000, Is64Bit = true },
            [0x6261] = new() { HwCode = 0x6261, ChipName = "MT6261", Description = "Feature Phone", WatchdogAddr = 0xA2050000, UartAddr = 0xA2090000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6580] = new() { HwCode = 0x6580, ChipName = "MT6580", Description = "Cortex-A7 Quad", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6592] = new() { HwCode = 0x6592, ChipName = "MT6592", Description = "Cortex-A7 Octa", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6752] = new() { HwCode = 0x6752, ChipName = "MT6752", Description = "Cortex-A53 Octa", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6755] = new() { HwCode = 0x6755, ChipName = "MT6755", Description = "Helio P10", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6765] = new() { HwCode = 0x6765, ChipName = "MT6765", Description = "Helio P35", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6768] = new() { HwCode = 0x6768, ChipName = "MT6768", Description = "Helio G80", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6771] = new() { HwCode = 0x6771, ChipName = "MT6771", Description = "Helio P70", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6785] = new() { HwCode = 0x6785, ChipName = "MT6785", Description = "Helio G90T", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x6795] = new() { HwCode = 0x6795, ChipName = "MT6795", Description = "Helio X10", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
            [0x8127] = new() { HwCode = 0x8127, ChipName = "MT8127", Description = "Tablet Cortex-A7", WatchdogAddr = 0x10007000, UartAddr = 0x11002000, BromPayloadAddr = 0x100A00, DaPayloadAddr = 0x200000 },
        };

        private static readonly Dictionary<ushort, int> _da1Addresses = new();
        private static readonly Dictionary<ushort, int> _da2Addresses = new();
        private static readonly Dictionary<ushort, int> _signatureLengths = new();
        private static readonly Dictionary<ushort, string> _exploitTypes = new();

        private static string _daFilePath = "";

        static MtkChipDatabase()
        {
            foreach (var kvp in _chips)
            {
                _da1Addresses[kvp.Key] = (int)kvp.Value.DaPayloadAddr;
                _da2Addresses[kvp.Key] = 0x40000000;
                _signatureLengths[kvp.Key] = kvp.Value.Da1SigLen;
                if (!string.IsNullOrEmpty(kvp.Value.ExploitType))
                    _exploitTypes[kvp.Key] = kvp.Value.ExploitType;
            }
        }

        public static void SetDaFilePath(string path) => _daFilePath = path;

        public static ChipRecord? GetChip(ushort hwCode)
        {
            return _chips.TryGetValue(hwCode, out var record) ? record : null;
        }

        public static MtkChipInfo ToChipInfo(ChipRecord record)
        {
            return new MtkChipInfo
            {
                HwCode = record.HwCode,
                ChipName = record.ChipName,
                Description = record.Description,
                WatchdogAddr = record.WatchdogAddr,
                UartAddr = record.UartAddr,
                BromPayloadAddr = record.BromPayloadAddr,
                DaPayloadAddr = record.DaPayloadAddr,
                CqDmaBase = record.CqDmaBase,
                DaMode = record.DaMode,
                SupportsXFlash = record.SupportsXFlash,
                RequiresSignature = record.RequiresSignature,
                Is64Bit = record.Is64Bit,
                ExploitType = record.ExploitType,
                HasExploit = !string.IsNullOrEmpty(record.ExploitType)
            };
        }

        public static uint GetDa1Address(ushort hwCode)
        {
            return _da1Addresses.TryGetValue(hwCode, out var addr) ? (uint)addr : 0x200000;
        }

        public static uint GetDa2Address(ushort hwCode)
        {
            return _da2Addresses.TryGetValue(hwCode, out var addr) ? (uint)addr : 0x40000000;
        }

        public static int GetSignatureLength(ushort hwCode, bool isDa2)
        {
            if (_signatureLengths.TryGetValue(hwCode, out var len))
                return isDa2 ? 0 : len;
            return isDa2 ? 0 : 0x1000;
        }

        public static DaMode GetDaMode(ushort hwCode)
        {
            if (_chips.TryGetValue(hwCode, out var record))
                return (DaMode)record.DaMode;
            return DaMode.Xml;
        }

        public static string GetExploitType(ushort hwCode)
        {
            return _exploitTypes.TryGetValue(hwCode, out var type) ? type : "";
        }

        public static bool IsChipPotentiallyVulnerable(ushort hwCode)
        {
            return _exploitTypes.ContainsKey(hwCode);
        }

        public static string GetChipVulnerabilityDescription(ushort hwCode)
        {
            if (_exploitTypes.TryGetValue(hwCode, out var type))
                return $"Chip 0x{hwCode:X4} supports {type} exploit";
            return "No known vulnerabilities";
        }
    }
}
