using System;

namespace v1per_wpf.MediaTek.Models
{
    public class MtkChipInfo
    {
        public ushort HwCode { get; set; }
        public ushort HwVer { get; set; }
        public ushort HwSubCode { get; set; }
        public ushort SwVer { get; set; }
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
        public bool BromPatched { get; set; }
        public bool RequiresLoader { get; set; }
        public string LoaderName { get; set; } = "";
        public string Codename { get; set; } = "";
        public string ExploitType { get; set; } = "";
        public bool HasExploit { get; set; }

        public string GetChipName()
        {
            if (!string.IsNullOrEmpty(ChipName))
                return ChipName;

            return HwCode switch
            {
                0x0279 => "MT6797",
                0x0321 => "MT6735",
                0x0326 => "MT6755",
                0x0335 => "MT6737",
                0x0507 => "MT6779",
                0x0551 => "MT6768",
                0x0562 => "MT6761",
                0x0570 => "MT6580",
                0x0571 => "MT6572",
                0x0572 => "MT6572",
                0x0588 => "MT6785",
                0x0600 => "MT6853",
                0x0601 => "MT6757",
                0x0688 => "MT6771",
                0x0690 => "MT6763",
                0x0699 => "MT6739",
                0x0707 => "MT6762",
                0x0717 => "MT6765",
                0x0725 => "MT6765",
                0x0766 => "MT6877",
                0x0788 => "MT6873",
                0x0813 => "MT6833",
                0x0816 => "MT6893",
                0x0886 => "MT6885",
                0x0950 => "MT6833",
                0x0959 => "MT6877",
                0x0989 => "MT6891",
                0x0996 => "MT6895",
                0x1172 => "MT6895",
                0x1186 => "MT6983",
                0x1208 => "MT6895",
                0x1209 => "MT6985",
                0x2502 => "MT2502",
                0x2503 => "MT2503",
                0x2601 => "MT2601",
                0x6261 => "MT6261",
                0x6570 => "MT6570",
                0x6575 => "MT6575",
                0x6577 => "MT6577",
                0x6580 => "MT6580",
                0x6582 => "MT6582",
                0x6589 => "MT6589",
                0x6592 => "MT6592",
                0x6595 => "MT6595",
                0x6752 => "MT6752",
                0x6753 => "MT6753",
                0x6755 => "MT6755",
                0x6757 => "MT6757",
                0x6761 => "MT6761",
                0x6763 => "MT6763",
                0x6765 => "MT6765",
                0x6768 => "MT6768",
                0x6771 => "MT6771",
                0x6779 => "MT6779",
                0x6785 => "MT6785",
                0x6795 => "MT6795",
                0x6797 => "MT6797",
                0x8127 => "MT8127",
                0x8135 => "MT8135",
                0x8163 => "MT8163",
                0x8167 => "MT8167",
                0x8168 => "MT8168",
                0x8173 => "MT8173",
                0x8176 => "MT8176",
                0x8695 => "MT8695",
                _ => $"MT{HwCode:X4}"
            };
        }

        public MtkChipInfo Clone() => (MtkChipInfo)MemberwiseClone();
    }

    public class MtkDeviceInfo
    {
        public string DevicePath { get; set; } = "";
        public string ComPort { get; set; } = "";
        public int Vid { get; set; }
        public int Pid { get; set; }
        public string Description { get; set; } = "";
        public bool IsDownloadMode { get; set; }
        public MtkChipInfo ChipInfo { get; set; } = new();
        public byte[]? MeId { get; set; }
        public byte[]? SocId { get; set; }
        public string MeIdHex => MeId != null ? BitConverter.ToString(MeId).Replace("-", "") : "";
        public string SocIdHex => SocId != null ? BitConverter.ToString(SocId).Replace("-", "") : "";
        public int DaMode { get; set; }
    }

    public class DaEntry
    {
        public string Name { get; set; } = "";
        public uint LoadAddr { get; set; }
        public int SignatureLen { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public bool Is64Bit { get; set; }
        public int Version { get; set; }
        public int DaType { get; set; }
    }

    public class MtkPartitionInfo
    {
        public string Name { get; set; } = "";
        public ulong StartSector { get; set; }
        public ulong SectorCount { get; set; }
        public ulong Size { get; set; }
        public string Type { get; set; } = "";
        public ulong Attributes { get; set; }
        public bool IsReadOnly => (Attributes & 0x1) != 0;
        public bool IsSystem => (Attributes & 0x2) != 0;

        public string SizeDisplay
        {
            get
            {
                if (Size >= 1024UL * 1024 * 1024)
                    return $"{Size / (1024.0 * 1024 * 1024):F2} GB";
                if (Size >= 1024 * 1024)
                    return $"{Size / (1024.0 * 1024):F2} MB";
                if (Size >= 1024)
                    return $"{Size / 1024.0:F2} KB";
                return $"{Size} B";
            }
        }
    }

    public class MtkFlashInfo
    {
        public string FlashType { get; set; } = "";
        public ushort ManufacturerId { get; set; }
        public ulong Capacity { get; set; }
        public uint BlockSize { get; set; }
        public uint PageSize { get; set; }
        public string Model { get; set; } = "";

        public string CapacityDisplay
        {
            get
            {
                if (Capacity >= 1024UL * 1024 * 1024 * 1024)
                    return $"{Capacity / (1024.0 * 1024 * 1024 * 1024):F2} TB";
                if (Capacity >= 1024UL * 1024 * 1024)
                    return $"{Capacity / (1024.0 * 1024 * 1024):F2} GB";
                if (Capacity >= 1024 * 1024)
                    return $"{Capacity / (1024.0 * 1024):F2} MB";
                return $"{Capacity} B";
            }
        }
    }

    public class MtkSecurityInfo
    {
        public bool SecureBootEnabled { get; set; }
        public bool IsUnfused { get; set; }
        public bool SlaEnabled { get; set; }
        public bool DaaEnabled { get; set; }
        public string MeId { get; set; } = "";
        public string SocId { get; set; } = "";
        public uint AntiRollbackVersion { get; set; }
        public bool IsLocked { get; set; }
        public bool SbcEnabled { get; set; }
    }

    public class MtkExploitInfo
    {
        public bool IsConnected { get; set; }
        public string ChipName { get; set; } = "";
        public ushort HwCode { get; set; }
        public string ExploitType { get; set; } = "None";
        public bool IsAllinoneSignatureSupported { get; set; }
        public bool IsCarbonaraSupported { get; set; }
    }

    public class ExploitResult
    {
        public bool Success { get; set; }
        public string UsedExploit { get; set; } = "";
        public string Message { get; set; } = "";
        public byte[]? ExtractedData { get; set; }
    }

    public class DaResult
    {
        public DaEntry? Da1 { get; set; }
        public DaEntry? Da2 { get; set; }
    }
}
