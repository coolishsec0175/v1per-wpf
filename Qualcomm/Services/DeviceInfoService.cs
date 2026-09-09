using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace V1Per.Qualcomm.Services
{
    public class DeviceFullInfo
    {
        public string ChipSerial { get; set; } = "";
        public string ChipName { get; set; } = "";
        public string HwId { get; set; } = "";
        public string PkHash { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string Brand { get; set; } = "";
        public string Model { get; set; } = "";
        public string Product { get; set; } = "";
        public string MarketName { get; set; } = "";
        public string AndroidVersion { get; set; } = "";
        public string SdkVersion { get; set; } = "";
        public string SecurityPatch { get; set; } = "";
        public string BuildId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string OtaVersion { get; set; } = "";
        public string DisplayId { get; set; } = "";
        public string StorageType { get; set; } = "";
        public string CurrentSlot { get; set; } = "";
        public string Codename { get; set; } = "";
        public Dictionary<string, string> AllProperties { get; set; } = new();

        public string DisplayName =>
            !string.IsNullOrEmpty(MarketName) ? MarketName :
            !string.IsNullOrEmpty(Brand) && !string.IsNullOrEmpty(Model) ? $"{Brand} {Model}" : Model;

        public string GetSummary()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(DisplayName)) sb.AppendLine($"Device: {DisplayName}");
            if (!string.IsNullOrEmpty(Codename)) sb.AppendLine($"Codename: {Codename}");
            if (!string.IsNullOrEmpty(ChipName)) sb.AppendLine($"Chip: {ChipName}");
            if (!string.IsNullOrEmpty(AndroidVersion)) sb.AppendLine($"Android: {AndroidVersion}");
            if (!string.IsNullOrEmpty(OtaVersion)) sb.AppendLine($"Version: {OtaVersion}");
            if (!string.IsNullOrEmpty(StorageType)) sb.AppendLine($"Storage: {StorageType.ToUpper()}");
            return sb.ToString().TrimEnd();
        }
    }

    public class DeviceInfoService
    {
        private readonly Action<string> _log;

        public DeviceInfoService(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public DeviceFullInfo ParseBuildPropFile(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            return ParseBuildProp(File.ReadAllText(filePath, Encoding.UTF8));
        }

        public DeviceFullInfo ParseBuildProp(string content)
        {
            var info = new DeviceFullInfo();
            if (string.IsNullOrEmpty(content)) return info;

            string[] lines;
            if (content.Contains('\0'))
            {
                var list = new List<string>();
                var matches = Regex.Matches(content, @"(ro|display|persist)\.[a-zA-Z0-9._-]+=[^\r\n\x00\s]+");
                foreach (Match m in matches) list.Add(m.Value);
                lines = list.ToArray();
            }
            else
            {
                lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                var idx = trimmed.IndexOf('=');
                if (idx <= 0) continue;

                var key = trimmed[..idx].Trim();
                var value = trimmed[(idx + 1)..].Trim();
                if (value.Length > 0 && (value[^1] < 32 || value[^1] > 126))
                    value = value.TrimEnd('\0', '\r', '\n', '\t', ' ');
                if (string.IsNullOrEmpty(value)) continue;

                info.AllProperties[key] = value;

                switch (key)
                {
                    case "ro.product.vendor.brand":
                    case "ro.product.brand":
                        if (string.IsNullOrEmpty(info.Brand) || value != "oplus") info.Brand = value;
                        break;
                    case "ro.vendor.oplus.market.name":
                    case "ro.vendor.oplus.market.enname":
                    case "ro.product.marketname":
                    case "ro.product.vendor.marketname":
                        if (string.IsNullOrEmpty(info.MarketName)) info.MarketName = value;
                        break;
                    case "ro.product.model":
                    case "ro.product.vendor.model":
                    case "ro.product.odm.model":
                        if (string.IsNullOrEmpty(info.Model)) info.Model = value;
                        break;
                    case "ro.build.display.id":
                    case "ro.build.display.id.show":
                        if (string.IsNullOrEmpty(info.OtaVersion)) info.OtaVersion = value;
                        if (key == "ro.build.display.id") info.DisplayId = value;
                        break;
                    case "ro.build.version.incremental":
                    case "ro.vendor.build.version.incremental":
                        if (string.IsNullOrEmpty(info.OtaVersion) || (value.StartsWith("V") || value.StartsWith("OS")))
                            info.OtaVersion = value;
                        break;
                    case "ro.build.version.release":
                    case "ro.vendor.build.version.release":
                        if (string.IsNullOrEmpty(info.AndroidVersion)) info.AndroidVersion = value;
                        break;
                    case "ro.build.version.sdk":
                        if (string.IsNullOrEmpty(info.SdkVersion)) info.SdkVersion = value;
                        break;
                    case "ro.build.version.security_patch":
                        if (string.IsNullOrEmpty(info.SecurityPatch)) info.SecurityPatch = value;
                        break;
                    case "ro.product.device":
                    case "ro.product.vendor.device":
                    case "ro.build.product":
                        if (string.IsNullOrEmpty(info.Codename)) info.Codename = value;
                        break;
                    case "ro.build.id":
                        info.BuildId = value;
                        break;
                    case "ro.build.fingerprint":
                    case "ro.vendor.build.fingerprint":
                        if (string.IsNullOrEmpty(info.Fingerprint)) info.Fingerprint = value;
                        break;
                }
            }

            if (string.IsNullOrEmpty(info.Codename) && !string.IsNullOrEmpty(info.Fingerprint))
            {
                var parts = info.Fingerprint.Split('/');
                if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[1]))
                    info.Codename = parts[1];
            }

            return info;
        }

        public static string DetectFileSystem(byte[] data)
        {
            if (data == null || data.Length < 512) return "unknown";

            if (data.Length >= 4)
            {
                uint magic0 = BitConverter.ToUInt32(data, 0);
                if (magic0 == 0xED26FF3A) return "sparse";
                if (magic0 == 0x73717368 || magic0 == 0x68737173) return "squashfs";
            }

            if (data.Length >= 1028)
            {
                uint erofs = BitConverter.ToUInt32(data, 1024);
                if (erofs == 0xE0F5E1E2) return "erofs";
            }

            if (data.Length >= 1082)
            {
                ushort ext4 = BitConverter.ToUInt16(data, 1080);
                if (ext4 == 0xEF53) return "ext4";
            }

            if (data.Length >= 1028)
            {
                uint f2fs = BitConverter.ToUInt32(data, 1024);
                if (f2fs == 0xF2F52010) return "f2fs";
            }

            if (data.Length >= 8 && data[0] == 'A' && data[1] == 'N' && data[2] == 'D' && data[3] == 'R')
                return "android_boot";

            return "unknown";
        }
    }
}
