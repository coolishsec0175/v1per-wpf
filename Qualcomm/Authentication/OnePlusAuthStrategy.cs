using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.Qualcomm;

namespace V1Per.Qualcomm.Authentication
{
    public class OnePlusAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;
        private string _serial = "123456";
        private string _projId = "";
        private int _version = 1;

        public string Name => "OnePlus (Demacia/SetProjModel)";

        private static readonly Dictionary<string, Tuple<int, string, int>> DeviceConfigs = new()
        {
            { "16859", Tuple.Create(1, (string)null, 0) },
            { "17801", Tuple.Create(1, (string)null, 0) },
            { "17819", Tuple.Create(1, (string)null, 0) },
            { "18801", Tuple.Create(1, (string)null, 0) },
            { "18811", Tuple.Create(1, (string)null, 0) },
            { "18857", Tuple.Create(1, (string)null, 0) },
            { "18821", Tuple.Create(1, (string)null, 0) },
            { "18825", Tuple.Create(1, (string)null, 0) },
            { "18827", Tuple.Create(1, (string)null, 0) },
            { "18831", Tuple.Create(1, (string)null, 0) },
            { "18865", Tuple.Create(1, (string)null, 0) },
            { "19801", Tuple.Create(1, (string)null, 0) },
            { "19861", Tuple.Create(1, (string)null, 0) },
            { "19863", Tuple.Create(1, (string)null, 0) },
            { "19821", Tuple.Create(2, "0cffee8a", 0) },
            { "19855", Tuple.Create(2, "6d9215b4", 0) },
            { "19867", Tuple.Create(2, "4107b2d4", 0) },
            { "19868", Tuple.Create(2, "178d8213", 0) },
            { "19811", Tuple.Create(2, "40217c07", 0) },
            { "19805", Tuple.Create(2, "1a5ec176", 0) },
            { "20809", Tuple.Create(2, "d6bc8c36", 0) },
            { "20801", Tuple.Create(2, "eacf50e7", 0) },
            { "20813", Tuple.Create(2, "48ad7b61", 0) },
            { "19815", Tuple.Create(2, "9c151c7f", 0) },
            { "19825", Tuple.Create(2, "0898dcd6", 0) },
            { "20828", Tuple.Create(2, "f498b60f", 0) },
            { "20885", Tuple.Create(3, "3a403a71", 1) },
            { "20886", Tuple.Create(3, "b8bd9e39", 1) },
            { "20888", Tuple.Create(3, "142f1bd7", 1) },
            { "20889", Tuple.Create(3, "f2056ae1", 1) },
            { "20880", Tuple.Create(3, "6ccf5913", 1) },
            { "20881", Tuple.Create(3, "fa9ff378", 1) },
            { "20882", Tuple.Create(3, "4ca1e84e", 1) },
            { "20883", Tuple.Create(3, "ad9dba4a", 1) },
        };

        private static readonly byte[] AesKeyPrefix1 = [0x10, 0x45, 0x63, 0x87, 0xE3, 0x7E, 0x23, 0x71];
        private static readonly byte[] AesKeySuffix1 = [0xA2, 0xD4, 0xA0, 0x74, 0x0F, 0xD3, 0x28, 0x96];
        private static readonly byte[] AesIv1 = [0x9D, 0x61, 0x4A, 0x1E, 0xAC, 0x81, 0xC9, 0xB2, 0xD3, 0x76, 0xD7, 0x49, 0x31, 0x03, 0x63, 0x79];
        private static readonly byte[] AesKeyPrefixDemacia = [0x01, 0x63, 0xA0, 0xD1, 0xFD, 0xE2, 0x67, 0x11];
        private static readonly byte[] AesKeySuffixDemacia = [0x48, 0x27, 0xC2, 0x08, 0xFB, 0xB0, 0xE6, 0xF0];
        private static readonly byte[] AesIvDemacia = [0x96, 0xE0, 0x79, 0x0C, 0xAE, 0x2B, 0xB4, 0xAF, 0x68, 0x4C, 0x36, 0xCB, 0x0B, 0xEC, 0x49, 0xCE];
        private static readonly byte[] AesKeyPrefixV3 = [0x46, 0xA5, 0x97, 0x30, 0xBB, 0x0D, 0x41, 0xE8];
        private static readonly byte[] AesIvV3 = [0xDC, 0x91, 0x0D, 0x88, 0xE3, 0xC6, 0xEE, 0x65, 0xF0, 0xC7, 0x44, 0xB4, 0x02, 0x30, 0xCE, 0x40];

        private const string ProdKeyOld = "b2fad511325185e5";
        private const string ProdKeyNew = "7016147d58e8c038";
        private const string RandomPostfixV1 = "8MwDdWXZO7sj0PF3";
        private const string RandomPostfixV3 = "c75oVnz8yUgLZObh";

        public OnePlusAuthStrategy(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public async Task<bool> AuthenticateAsync(Comm comm, string programmerPath, CancellationToken ct = default)
        {
            _log("[OnePlus] Starting authentication process...");

            if (!string.IsNullOrEmpty(comm.chipNum))
            {
                string serialHex = comm.chipNum.Replace("0x", "");
                if (uint.TryParse(serialHex, NumberStyles.HexNumber, null, out uint s))
                    _serial = s.ToString();
            }

            _log($"[OnePlus] Serial: {_serial}");
            await ReadProjIdAsync(comm, ct);

            if (string.IsNullOrEmpty(_projId))
            {
                _log("[OnePlus] Unable to get projid, using default 18821");
                _projId = "18821";
            }

            if (await TryAuthenticateWithProjIdAsync(comm, _projId, ct))
                return true;

            var alternatives = GetAlternativeProjIds(_projId);
            foreach (var altProjId in alternatives)
            {
                if (ct.IsCancellationRequested) break;
                _log($"[OnePlus] Trying alternative projid: {altProjId}");
                if (await TryAuthenticateWithProjIdAsync(comm, altProjId, ct))
                {
                    _projId = altProjId;
                    return true;
                }
            }

            _log("[OnePlus] All authentication attempts failed");
            return false;
        }

        private async Task ReadProjIdAsync(Comm comm, CancellationToken ct)
        {
            try
            {
                _log("[OnePlus] Reading ProjId...");
                comm.SendCommand("<?xml version=\"1.0\" ?><data><getprjversion /></data>", checkAck: false);
                await Task.Delay(200, ct);
                comm.GetResponse(waiteACK: false);

                string response = comm.auth;
                if (!string.IsNullOrEmpty(response))
                {
                    var match = Regex.Match(response, @"(?:prjversion|PrjVersion|projid)=""(\d+)""", RegexOptions.IgnoreCase);
                    if (match.Success && match.Groups[1].Value.Length >= 5)
                    {
                        _projId = match.Groups[1].Value;
                        _log($"[OnePlus] Obtained ProjId: {_projId}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"[OnePlus] getprjversion exception: {ex.Message}");
            }

            string pkHash = (comm.auth ?? "").ToLower();
            if (pkHash.StartsWith("2acf3a85") || pkHash.StartsWith("8aabc662"))
            {
                _projId = "18821";
                _log("[OnePlus] Guessed device as OP7 Pro (SM8150)");
            }
            else if (pkHash.StartsWith("c0c66e27"))
            {
                _projId = "18801";
                _log("[OnePlus] Guessed device as OP6T (SDM845)");
            }
        }

        private List<string> GetAlternativeProjIds(string primaryProjId)
        {
            var alts = new List<string>();
            if (primaryProjId is "18821" or "18825" or "18827" or "18831")
                alts.AddRange(["18821", "18825", "18827", "18831", "18865", "19801"]);
            else if (primaryProjId is "18865" or "19801" or "19863" or "19861")
                alts.AddRange(["18865", "19801", "19863", "19861", "18821", "18825"]);
            else if (primaryProjId is "18801" or "18811" or "17819" or "17801")
                alts.AddRange(["18801", "18811", "17819", "17801"]);
            alts.Remove(primaryProjId);
            return alts;
        }

        private async Task<bool> TryAuthenticateWithProjIdAsync(Comm comm, string projId, CancellationToken ct)
        {
            DeviceConfigs.TryGetValue(projId, out var config);
            config ??= Tuple.Create(1, (string)null, 0);

            _version = config.Item1;
            string modelId = config.Item2 ?? projId;
            _log($"[OnePlus] Trying: ProjId={projId}, Algorithm=V{_version}");

            try
            {
                if (_version == 3)
                    return await AuthenticateV3Async(comm, modelId, ct);
                return await AuthenticateV1V2Async(comm, modelId, ct);
            }
            catch (Exception ex)
            {
                _log($"[OnePlus] Authentication error: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> AuthenticateV1V2Async(Comm comm, string modelId, CancellationToken ct)
        {
            string pk = GenerateRandomPk();
            string prodKey = _projId is "18825" or "18801" ? ProdKeyOld : ProdKeyNew;

            _log("[OnePlus] Step 1: demacia verification...");
            var demacia = GenerateDemaciaToken(pk);
            string demCmd = $"demacia token=\"{demacia.Item2}\" pk=\"{demacia.Item1}\"";
            comm.SendCommand(demCmd, checkAck: false);
            await Task.Delay(200, ct);
            comm.GetResponse(waiteACK: false);

            _log($"[OnePlus] Step 2: setprojmodel (model={modelId})...");
            var proj = GenerateSetProjModelToken(modelId, pk, prodKey);
            string projCmd = $"setprojmodel token=\"{proj.Item2}\" pk=\"{proj.Item1}\"";
            comm.SendCommand(projCmd, checkAck: false);
            await Task.Delay(200, ct);
            bool ok = comm.SendCommand(projCmd, checkAck: true);

            if (ok)
                _log("[OnePlus] Authentication successful! Device unlocked.");
            return ok;
        }

        private async Task<bool> AuthenticateV3Async(Comm comm, string modelId, CancellationToken ct)
        {
            _log("[OnePlus] Step 1: Getting device timestamp...");
            comm.SendCommand("setprocstart", checkAck: false);
            await Task.Delay(200, ct);
            comm.GetResponse(waiteACK: false);
            string timestamp = ExtractAttribute(comm.auth, "device_timestamp");
            if (string.IsNullOrEmpty(timestamp)) return false;

            _log($"[OnePlus] Step 2: setswprojmodel (model={modelId})...");
            string pk = GenerateRandomPk();
            string prodKey = _projId is "18825" or "18801" ? ProdKeyOld : ProdKeyNew;

            var sw = GenerateSetSwProjModelToken(modelId, pk, prodKey, timestamp);
            string swCmd = $"setswprojmodel token=\"{sw.Item2}\" pk=\"{sw.Item1}\"";
            comm.SendCommand(swCmd, checkAck: false);
            await Task.Delay(200, ct);
            bool ok = comm.SendCommand(swCmd, checkAck: true);

            if (ok)
                _log("[OnePlus] Authentication successful! Device unlocked.");
            return ok;
        }

        private static string GenerateRandomPk()
        {
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            var random = new Random();
            var sb = new StringBuilder(16);
            for (int i = 0; i < 16; i++) sb.Append(chars[random.Next(chars.Length)]);
            return sb.ToString();
        }

        private Tuple<string, string> GenerateDemaciaToken(string pk)
        {
            string serial = _serial.PadLeft(10, '0');
            string hashSource = "2e7006834dafe8ad" + serial + "a6674c6b039707ff";
            byte[] hashBytes = ComputeSha256(Encoding.UTF8.GetBytes(hashSource));

            byte[] data = new byte[256];
            Encoding.ASCII.GetBytes("907heavyworkload").CopyTo(data, 0);
            hashBytes.CopyTo(data, 16);

            return Tuple.Create(pk, EncryptAesCbc(data, pk, true));
        }

        private Tuple<string, string> GenerateSetProjModelToken(string modelId, string pk, string prodKey)
        {
            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string h1 = prodKey + modelId + RandomPostfixV1;
            string modelHash = BytesToHex(ComputeSha256(Encoding.UTF8.GetBytes(h1))).ToUpper();

            string version = "guacamoles_21_O.22_191107";
            string h2 = "c4b95538c57df231" + modelId + "0" + _serial + version + ts + modelHash + "5b0217457e49381b";
            string secret = BytesToHex(ComputeSha256(Encoding.UTF8.GetBytes(h2))).ToUpper();

            string dataStr = $"{modelId},{RandomPostfixV1},{modelHash},{version},0,{_serial},{ts},{secret}";
            byte[] padded = new byte[256];
            Encoding.UTF8.GetBytes(dataStr).CopyTo(padded, 0);

            return Tuple.Create(pk, EncryptAesCbc(padded, pk, false));
        }

        private Tuple<string, string> GenerateSetSwProjModelToken(string modelId, string pk, string prodKey, string deviceTs)
        {
            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string h1 = prodKey + modelId + RandomPostfixV3;
            string modelHash = BytesToHex(ComputeSha256(Encoding.UTF8.GetBytes(h1))).ToUpper();

            string version = "billie8_14_E.01_201028";
            string h2 = prodKey + modelId + _serial + version + ts + modelHash + "8f7359c8a2951e8c";
            string secret = BytesToHex(ComputeSha256(Encoding.UTF8.GetBytes(h2))).ToUpper();

            string deviceId = modelId;
            try { deviceId = Convert.ToInt32(modelId, 16).ToString(); } catch { }

            string dataStr = $"{modelId},{RandomPostfixV3},{modelHash},0,0,{version},{_serial},{deviceId},{ts},{secret}";
            byte[] padded = new byte[512];
            Encoding.UTF8.GetBytes(dataStr).CopyTo(padded, 0);

            return Tuple.Create(pk, EncryptAesCbcV3(padded, pk, deviceTs));
        }

        private static string EncryptAesCbc(byte[] data, string pk, bool isDemacia)
        {
            byte[] keyPrefix = isDemacia ? AesKeyPrefixDemacia : AesKeyPrefix1;
            byte[] keySuffix = isDemacia ? AesKeySuffixDemacia : AesKeySuffix1;
            byte[] iv = isDemacia ? AesIvDemacia : AesIv1;

            byte[] key = new byte[32];
            keyPrefix.CopyTo(key, 0);
            Encoding.UTF8.GetBytes(pk).CopyTo(key, 8);
            keySuffix.CopyTo(key, 24);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            byte[] encrypted = aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
            return BytesToHex(encrypted).ToUpper();
        }

        private static string EncryptAesCbcV3(byte[] data, string pk, string deviceTs)
        {
            byte[] key = new byte[32];
            AesKeyPrefixV3.CopyTo(key, 0);
            Encoding.UTF8.GetBytes(pk).CopyTo(key, 8);
            BitConverter.GetBytes(long.Parse(deviceTs)).CopyTo(key, 24);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = AesIvV3;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            byte[] encrypted = aes.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
            return BytesToHex(encrypted).ToUpper();
        }

        private static byte[] ComputeSha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string ExtractAttribute(string xml, string attrName)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            int start = xml.IndexOf(attrName + "=\"");
            if (start < 0) return null;
            start += attrName.Length + 2;
            int end = xml.IndexOf("\"", start);
            return end < 0 ? null : xml.Substring(start, end - start);
        }
    }
}
