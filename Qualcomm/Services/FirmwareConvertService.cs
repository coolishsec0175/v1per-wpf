using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace V1Per.Qualcomm.Services
{
    public enum FirmwareFormat
    {
        Unknown,
        OFP,
        OZIP,
        OPS,
        ZIP,
        TGZ
    }

    public class ConvertResult
    {
        public bool Success { get; set; }
        public FirmwareFormat SourceFormat { get; set; }
        public string OutputDir { get; set; } = "";
        public int FilesExtracted { get; set; }
        public string Message { get; set; } = "";
    }

    public class FirmwareConvertService
    {
        private readonly Action<string> _log;

        private static readonly byte[] OzipMagic = "OZIP"u8.ToArray();
        private static readonly byte[] OpsMagic = [0x4F, 0x50, 0x53, 0x00];
        private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

        public FirmwareConvertService(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public FirmwareFormat DetectFormat(string filePath)
        {
            if (!File.Exists(filePath)) return FirmwareFormat.Unknown;

            byte[] header = new byte[16];
            using (var fs = File.OpenRead(filePath))
            {
                int read = fs.Read(header, 0, header.Length);
                if (read < 4) return FirmwareFormat.Unknown;
            }

            if (header.Take(4).SequenceEqual(OzipMagic)) return FirmwareFormat.OZIP;
            if (header.Take(4).SequenceEqual(OpsMagic)) return FirmwareFormat.OPS;
            if (header.Take(4).SequenceEqual(ZipMagic)) return FirmwareFormat.ZIP;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".ofp") return FirmwareFormat.OFP;
            if (ext == ".tgz" || ext == ".tar.gz") return FirmwareFormat.TGZ;

            return FirmwareFormat.Unknown;
        }

        public ConvertResult ConvertFirmware(string inputPath, string outputDir)
        {
            var result = new ConvertResult();
            result.SourceFormat = DetectFormat(inputPath);

            _log($"[Convert] Detected format: {result.SourceFormat}");
            _log($"[Convert] Input: {inputPath}");

            if (string.IsNullOrEmpty(outputDir))
                outputDir = Path.Combine(Path.GetDirectoryName(inputPath) ?? "", Path.GetFileNameWithoutExtension(inputPath) + "_converted");

            try
            {
                switch (result.SourceFormat)
                {
                    case FirmwareFormat.OZIP:
                        result = ConvertOzip(inputPath, outputDir);
                        break;
                    case FirmwareFormat.OPS:
                        result = ConvertOps(inputPath, outputDir);
                        break;
                    case FirmwareFormat.OFP:
                        result = ConvertOfp(inputPath, outputDir);
                        break;
                    case FirmwareFormat.ZIP:
                        result = ConvertZip(inputPath, outputDir);
                        break;
                    case FirmwareFormat.TGZ:
                        result = ConvertTgz(inputPath, outputDir);
                        break;
                    default:
                        result.Message = $"Unsupported format: {Path.GetExtension(inputPath)}";
                        _log($"[Convert] {result.Message}");
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Message = $"Conversion failed: {ex.Message}";
                _log($"[Convert] {result.Message}");
            }

            return result;
        }

        private ConvertResult ConvertOzip(string inputPath, string outputDir)
        {
            var result = new ConvertResult { SourceFormat = FirmwareFormat.OZIP };
            _log("[Convert] OZIP files are encrypted Oppo/Realme firmware packages.");
            _log("[Convert] OZIP decryption requires device-specific keys.");

            Directory.CreateDirectory(outputDir);

            using var fs = File.OpenRead(inputPath);
            byte[] header = new byte[16];
            fs.Read(header, 0, 16);

            byte[] payloadSizeBytes = new byte[8];
            fs.Read(payloadSizeBytes, 0, 8);
            long payloadSize = BitConverter.ToInt64(payloadSizeBytes, 0);

            _log($"[Convert] OZIP header detected, payload size: {payloadSize} bytes");
            _log("[Convert] OZIP decryption is not yet implemented - requires vendor-specific AES keys.");

            result.Message = "OZIP format detected but decryption requires vendor-specific keys. Use MSMDownloadTool or Extract Samsung ROM for Oppo/Realme firmware.";
            result.OutputDir = outputDir;
            return result;
        }

        private ConvertResult ConvertOps(string inputPath, string outputDir)
        {
            var result = new ConvertResult { SourceFormat = FirmwareFormat.OPS };
            _log("[Convert] OPS files are encrypted OnePlus firmware packages.");

            Directory.CreateDirectory(outputDir);

            using var fs = File.OpenRead(inputPath);
            byte[] header = new byte[16];
            fs.Read(header, 0, 16);

            _log("[Convert] OPS format detected.");
            _log("[Convert] OPS decryption requires device-specific keys from MSMDownloadTool.");

            result.Message = "OPS format detected. OPS files are encrypted packages - use MSMDownloadTool or payload-dumper to extract.";
            result.OutputDir = outputDir;
            return result;
        }

        private ConvertResult ConvertOfp(string inputPath, string outputDir)
        {
            var result = new ConvertResult { SourceFormat = FirmwareFormat.OFP };
            _log("[Convert] OFP files are Oppo/Realme firmware packages.");

            Directory.CreateDirectory(outputDir);

            try
            {
                using var fs = File.OpenRead(inputPath);
                byte[] header = new byte[64];
                fs.Read(header, 0, 64);

                string magic = Encoding.ASCII.GetString(header, 0, 4);
                _log($"[Convert] OFP magic: {magic}");

                result.Message = "OFP format detected. OFP files may need key-based decryption. Try using ofp_extract or MSMDownloadTool.";
                result.OutputDir = outputDir;
            }
            catch (Exception ex)
            {
                result.Message = $"OFP detection failed: {ex.Message}";
            }
            return result;
        }

        private ConvertResult ConvertZip(string inputPath, string outputDir)
        {
            var result = new ConvertResult { SourceFormat = FirmwareFormat.ZIP };
            Directory.CreateDirectory(outputDir);

            try
            {
                ZipFile.ExtractToDirectory(inputPath, outputDir, overwriteFiles: true);
                result.FilesExtracted = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories).Length;
                result.Success = true;
                result.OutputDir = outputDir;
                result.Message = $"Extracted {result.FilesExtracted} files";
                _log($"[Convert] ZIP extracted: {result.FilesExtracted} files to {outputDir}");
            }
            catch (Exception ex)
            {
                result.Message = $"ZIP extraction failed: {ex.Message}";
            }
            return result;
        }

        private ConvertResult ConvertTgz(string inputPath, string outputDir)
        {
            var result = new ConvertResult { SourceFormat = FirmwareFormat.TGZ };
            Directory.CreateDirectory(outputDir);

            try
            {
                using var fs = File.OpenRead(inputPath);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                using var tar = new MemoryStream();
                gzip.CopyTo(tar);
                tar.Position = 0;

                result.Message = "TGZ decompressed. Tar extraction requires additional implementation.";
                result.OutputDir = outputDir;
            }
            catch (Exception ex)
            {
                result.Message = $"TGZ extraction failed: {ex.Message}";
            }
            return result;
        }

        public string GetFormatDescription(FirmwareFormat format) => format switch
        {
            FirmwareFormat.OFP => "Oppo/Realme Firmware Package - may require decryption keys",
            FirmwareFormat.OZIP => "Oppo/Realme ZIP - encrypted, needs vendor AES keys",
            FirmwareFormat.OPS => "OnePlus Software Package - encrypted, needs MSMDownloadTool",
            FirmwareFormat.ZIP => "Standard ZIP archive - can be extracted directly",
            FirmwareFormat.TGZ => "Gzipped TAR archive",
            _ => "Unknown format"
        };
    }
}
