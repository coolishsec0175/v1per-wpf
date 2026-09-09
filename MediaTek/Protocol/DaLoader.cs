using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.MediaTek.Common;
using v1per_wpf.MediaTek.Database;
using v1per_wpf.MediaTek.Models;

namespace v1per_wpf.MediaTek.Protocol
{
    public class DaLoader
    {
        private readonly BromClient _brom;
        private readonly Action<string> _log;
        private readonly Action<double> _progress;

        public DaLoader(BromClient brom, Action<string> log, Action<double>? progress = null)
        {
            _brom = brom;
            _log = log;
            _progress = progress ?? (_ => { });
        }

        public DaResult? ParseDaFile(string daFilePath, ushort hwCode)
        {
            if (!File.Exists(daFilePath))
            {
                _log($"[MTK-DA] DA file not found: {daFilePath}");
                return null;
            }

            byte[] daData = File.ReadAllBytes(daFilePath);
            _log($"[MTK-DA] Loaded DA file: {Path.GetFileName(daFilePath)} ({daData.Length} bytes)");

            int sigLen = DetectSignatureLength(daData);
            int daMode = (int)MtkChipDatabase.GetDaMode(hwCode);

            uint da1Addr = _brom.ChipInfo.DaPayloadAddr;
            if (da1Addr == 0) da1Addr = MtkChipDatabase.GetDa1Address(hwCode);

            var da1 = new DaEntry
            {
                Name = "DA1",
                LoadAddr = da1Addr,
                SignatureLen = sigLen,
                Data = daData,
                DaType = daMode
            };

            _log($"[MTK-DA] DA1: {daData.Length} bytes, addr=0x{da1Addr:X}, sig=0x{sigLen:X}");
            return new DaResult { Da1 = da1 };
        }

        public async Task<bool> UploadDa1Async(DaEntry da, CancellationToken ct = default)
        {
            _log($"[MTK-DA] Uploading DA1 to 0x{da.LoadAddr:X8}...");

            _progress(10);
            bool sent = await _brom.SendDaAsync(da.LoadAddr, da.Data, da.SignatureLen, ct);
            if (!sent)
            {
                _log("[MTK-DA] DA1 upload failed");
                return false;
            }
            _progress(50);

            _log("[MTK-DA] Jumping to DA1...");
            bool jumped = await _brom.JumpDaAsync(da.LoadAddr, ct);
            if (!jumped)
            {
                _log("[MTK-DA] DA1 jump failed");
                return false;
            }
            _progress(100);

            _log("[MTK-DA] DA1 uploaded successfully");
            return true;
        }

        public async Task<bool> UploadDa2Async(DaEntry da, XmlDaClient xmlClient, CancellationToken ct = default)
        {
            _log($"[MTK-DA] Uploading DA2 ({da.Data.Length} bytes)...");
            _progress(50);

            bool written = await xmlClient.WritePartitionAsync("da2", da.Data, ct);
            _progress(100);

            if (written)
                _log("[MTK-DA] DA2 uploaded successfully");
            else
                _log("[MTK-DA] DA2 upload failed");

            return written;
        }

        private int DetectSignatureLength(byte[] data)
        {
            if (data.Length < 4) return 0;

            if (data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F')
            {
                return MtkChipDatabase.GetSignatureLength(_brom.HwCode, false);
            }

            if (data[3] == 0xEA || data[3] == 0xEB)
            {
                return 0x100;
            }

            if (data.Length >= 8)
            {
                string header = Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length));
                if (header.Contains("MTK") || header.Contains("hvea"))
                {
                    return 0x1000;
                }
            }

            return MtkChipDatabase.GetSignatureLength(_brom.HwCode, false);
        }

        public static byte[] ProcessDaData(byte[] data)
        {
            return data;
        }
    }
}
