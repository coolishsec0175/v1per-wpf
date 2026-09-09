using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.Qualcomm;

namespace V1Per.Qualcomm.Authentication
{
    public class XiaomiAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;

        public string Name => "Xiaomi (MiAuth Bypass)";

        private static readonly string[] AuthSignsBase64 =
        [
            "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso" +
            "+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZOlfn4wUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT" +
            "/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN" +
            "/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",

            "vzXWATo51hZr4Dh+a5sA/Q4JYoP4Ee3oFZSGbPZ2tBsaMupn" +
            "+6tPbZDkXJRLUzAqHaMtlPMKaOHrEWZysCkgCJqpOPkUZNaSbEKpPQ6uiOVJpJwA" +
            "/PmxuJ72inzSPevriMAdhQrNUqgyu4ATTEsOKnoUIuJTDBmzCeuh/34SOjTdO4Pc+s3ORfMD0TX+WImeUx4c9xVdSL/xirPl" +
            "/BouhfuwFd4qPPyO5RqkU/fevEoJWGHaFjfI302c9k7EpfRUhq1z+wNpZblOHuj0B3/7VOkK8KtSvwLkmVF" +
            "/t9ECiry6G5iVGEOyqMlktNlIAbr2MMYXn6b4Y3GDCkhPJ5LUkQ=="
        ];

        public XiaomiAuthStrategy(Action<string> log = null)
        {
            _log = log ?? (_ => { });
        }

        public async Task<bool> AuthenticateAsync(Comm comm, string programmerPath, CancellationToken ct = default)
        {
            _log("[MiAuth] Attempting Xiaomi authorization bypass...");

            try
            {
                int index = 1;
                foreach (var base64 in AuthSignsBase64)
                {
                    if (ct.IsCancellationRequested) break;

                    _log($"[MiAuth] Trying signature library #{index}...");

                    string sigCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"256\" verbose=\"1\"/></data>";
                    comm.SendCommand(sigCmd, checkAck: false);
                    await Task.Delay(100, ct);

                    byte[] data = Convert.FromBase64String(base64);
                    comm.WritePort(data, 0, data.Length);
                    comm.GetResponse(waiteACK: true);

                    string resp = comm.auth;
                    if (!string.IsNullOrEmpty(resp) && (resp.Contains("ACK") || resp.Contains("authenticated")))
                    {
                        _log("[MiAuth] Bypass successful! Device unlocked.");
                        return true;
                    }
                    index++;
                }

                _log("[MiAuth] Built-in signatures invalid.");
                return false;
            }
            catch (Exception ex)
            {
                _log($"[MiAuth] Exception: {ex.Message}");
                return false;
            }
        }

        public string GetAuthToken(Comm comm)
        {
            try
            {
                string reqCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"req\" /></data>";
                comm.SendCommand(reqCmd, checkAck: false);
                comm.GetResponse(waiteACK: false);

                string rawValue = ExtractAttribute(comm.auth, "value");
                if (string.IsNullOrEmpty(rawValue)) return null;

                if (rawValue.StartsWith("VQ")) return rawValue;

                byte[] tokenBytes = HexToBytes(rawValue);
                if (tokenBytes != null && tokenBytes.Length > 0)
                {
                    string base64Token = Convert.ToBase64String(tokenBytes);
                    return base64Token.StartsWith("VQ") ? base64Token : base64Token;
                }

                return rawValue;
            }
            catch { return null; }
        }

        public bool AuthenticateWithSignature(Comm comm, string signatureBase64)
        {
            try
            {
                string sigCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"256\" verbose=\"1\"/></data>";
                comm.SendCommand(sigCmd, checkAck: false);
                comm.GetResponse(waiteACK: false);

                byte[] signatureData = Convert.FromBase64String(signatureBase64);
                comm.WritePort(signatureData, 0, signatureData.Length);
                comm.GetResponse(waiteACK: true);

                return comm.auth != null && (comm.auth.Contains("ACK") || comm.auth.Contains("authenticated"));
            }
            catch { return false; }
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

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            if (hex.Length % 2 != 0) return null;
            try
            {
                byte[] bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return bytes;
            }
            catch { return null; }
        }
    }
}
