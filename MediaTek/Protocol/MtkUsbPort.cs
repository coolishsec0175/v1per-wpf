using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace v1per_wpf.MediaTek.Protocol
{
    public class MtkUsbPort : IDisposable
    {
        private string _backendPath = "";
        private bool _isConnected;
        private int _hwCode;
        private int _hwSubCode;
        private int _blVersion;
        private int _targetConfig;

        public bool IsOpen => _isConnected;
        public string PortName => "USB (MTK Backend)";
        public int HwCode => _hwCode;
        public int HwSubCode => _hwSubCode;
        public int BlVersion => _blVersion;
        public int TargetConfig => _targetConfig;

        public MtkUsbPort()
        {
            _backendPath = FindBackend();
        }

        private string FindBackend()
        {
            var assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            var candidates = new[]
            {
                Path.Combine(assemblyDir, "mtk-backend.exe"),
                Path.Combine(assemblyDir, "mtk-backend", "mtk-backend.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mtk-backend.exe"),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;

            return "mtk-backend.exe";
        }

        public static async Task<MtkUsbPort?> FindAndOpenAsync(CancellationToken ct = default)
        {
            var port = new MtkUsbPort();
            if (!File.Exists(port._backendPath))
                return null;

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var detectResult = await RunBackend("detect", ct);
                if (detectResult != null)
                {
                    using var doc = JsonDocument.Parse(detectResult);
                    var root = doc.RootElement;
                    if (root.GetProperty("status").GetString() == "ok")
                    {
                        var connectResult = await RunBackend("connect", ct);
                        if (connectResult != null)
                        {
                            using var cdoc = JsonDocument.Parse(connectResult);
                            var croot = cdoc.RootElement;
                            if (croot.TryGetProperty("handshake", out var hs) && hs.GetProperty("success").GetBoolean())
                            {
                                port._isConnected = true;
                                if (hs.TryGetProperty("hw_code", out var hc) && hc.ValueKind != JsonValueKind.Null)
                                    port._hwCode = hc.GetInt32();
                                if (hs.TryGetProperty("hw_sub_code", out var hsc) && hsc.ValueKind != JsonValueKind.Null)
                                    port._hwSubCode = hsc.GetInt32();
                                if (hs.TryGetProperty("bl_version", out var bl) && bl.ValueKind != JsonValueKind.Null)
                                    port._blVersion = bl.GetInt32();
                                if (hs.TryGetProperty("target_config", out var tc) && tc.ValueKind != JsonValueKind.Null)
                                    port._targetConfig = tc.GetInt32();
                                return port;
                            }
                        }
                    }
                }
                await Task.Delay(500, ct);
            }
            return null;
        }

        private static async Task<string?> RunBackend(string args, CancellationToken ct)
        {
            try
            {
                var port = new MtkUsbPort();
                var psi = new ProcessStartInfo
                {
                    FileName = port._backendPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => { try { process.Kill(); } catch { } });

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return output;
            }
            catch
            {
                return null;
            }
        }

        public void Write(byte[] data, int offset, int count)
        {
        }

        public int Read(byte[] buffer, int offset, int count, int timeoutMs = 1000)
        {
            return 0;
        }

        public byte ReadByte(int timeoutMs = 1000)
        {
            return 0;
        }

        public void DiscardBuffers() { }

        public void Close()
        {
            _isConnected = false;
        }

        public void Dispose()
        {
            Close();
        }
    }
}
