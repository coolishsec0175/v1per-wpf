using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.MediaTek.Models;

namespace v1per_wpf.MediaTek.Protocol
{
    public class XmlDaClient : IDisposable
    {
        private readonly MtkUsbPort _port;
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly SemaphoreSlim _portLock;

        public bool IsConnected { get; private set; }

        public XmlDaClient(MtkUsbPort port, Action<string> log, Action<string> logDetail,
                           SemaphoreSlim portLock)
        {
            _port = port;
            _log = log;
            _logDetail = logDetail;
            _portLock = portLock;
            IsConnected = true;
        }

        public async Task<bool> WaitForDaReadyAsync(int timeoutMs, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return false;
                try
                {
                    var buf = new byte[4096];
                    int read = _port.Read(buf, 0, buf.Length, 100);
                    if (read > 0)
                    {
                        string text = Encoding.ASCII.GetString(buf, 0, read);
                        _logDetail($"[MTK-XML] Received: {text.Trim()}");
                        if (text.Contains("OK") || text.Contains("SYNC"))
                        {
                            _log("[MTK-XML] DA1 ready");
                            return true;
                        }
                    }
                    await Task.Delay(50, ct);
                }
                catch { await Task.Delay(100, ct); }
            }
            return false;
        }

        public async Task<bool> SetRuntimeParametersAsync(CancellationToken ct = default)
        {
            _logDetail("[MTK-XML] Setting runtime parameters");
            return await SendXmlCommandAsync("runtime", "OK", ct);
        }

        public async Task<List<MtkPartitionInfo>?> ReadPartitionTableAsync(CancellationToken ct = default)
        {
            _log("[MTK-XML] Reading partition table...");
            string response = await SendXmlCommandWithResponseAsync("read_table", ct);
            if (string.IsNullOrEmpty(response)) return null;

            var partitions = new List<MtkPartitionInfo>();
            string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 4)
                {
                    partitions.Add(new MtkPartitionInfo
                    {
                        Name = parts[0].Trim(),
                        StartSector = ulong.TryParse(parts[1].Trim(), out var ss) ? ss : 0,
                        SectorCount = ulong.TryParse(parts[2].Trim(), out var sc) ? sc : 0,
                        Size = ulong.TryParse(parts[3].Trim(), out var sz) ? sz : 0,
                        Type = parts.Length > 4 ? parts[4].Trim() : ""
                    });
                }
            }

            _log($"[MTK-XML] Found {partitions.Count} partitions");
            return partitions;
        }

        public async Task<byte[]?> ReadPartitionAsync(string name, ulong size, CancellationToken ct = default)
        {
            _log($"[MTK-XML] Reading partition: {name}");
            string cmd = $"read {name} {size}";
            return await SendBinaryCommandAsync(cmd, (int)size, ct);
        }

        public async Task<bool> WritePartitionAsync(string name, byte[] data, CancellationToken ct = default)
        {
            _log($"[MTK-XML] Writing partition: {name} ({data.Length} bytes)");
            string cmd = $"write {name} {data.Length}";
            return await SendBinaryWriteCommandAsync(cmd, data, ct);
        }

        public async Task<bool> ErasePartitionAsync(string name, CancellationToken ct = default)
        {
            _log($"[MTK-XML] Erasing partition: {name}");
            return await SendXmlCommandAsync($"erase {name}", "OK", ct);
        }

        public async Task<bool> RebootAsync(CancellationToken ct = default)
        {
            _log("[MTK-XML] Rebooting device");
            return await SendXmlCommandAsync("reboot", "OK", ct);
        }

        public async Task<bool> ShutdownAsync(CancellationToken ct = default)
        {
            _log("[MTK-XML] Shutting down device");
            return await SendXmlCommandAsync("shutdown", "OK", ct);
        }

        public async Task<MtkFlashInfo?> GetFlashInfoAsync(CancellationToken ct = default)
        {
            _log("[MTK-XML] Getting flash info");
            string response = await SendXmlCommandWithResponseAsync("flash_info", ct);
            if (string.IsNullOrEmpty(response)) return null;

            string[] parts = response.Split('|');
            if (parts.Length >= 3)
            {
                return new MtkFlashInfo
                {
                    FlashType = parts[0].Trim(),
                    ManufacturerId = ushort.TryParse(parts[1].Trim(), out var mid) ? mid : (ushort)0,
                    Capacity = ulong.TryParse(parts[2].Trim(), out var cap) ? cap : 0,
                    Model = parts.Length > 3 ? parts[3].Trim() : ""
                };
            }
            return null;
        }

        private async Task<bool> SendXmlCommandAsync(string command, string expectedResponse, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(command + "\n");
                _port.Write(cmdBytes, 0, cmdBytes.Length);

                var response = await ReadResponseAsync(5000, ct);
                return response?.Contains(expectedResponse) ?? false;
            }
            catch (Exception ex)
            {
                _log($"[MTK-XML] Command error: {ex.Message}");
                return false;
            }
            finally { _portLock.Release(); }
        }

        private async Task<string?> SendXmlCommandWithResponseAsync(string command, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(command + "\n");
                _port.Write(cmdBytes, 0, cmdBytes.Length);
                return await ReadResponseAsync(10000, ct);
            }
            catch (Exception ex)
            {
                _log($"[MTK-XML] Command error: {ex.Message}");
                return null;
            }
            finally { _portLock.Release(); }
        }

        private async Task<byte[]?> SendBinaryCommandAsync(string command, int expectedSize, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(command + "\n");
                _port.Write(cmdBytes, 0, cmdBytes.Length);

                var ack = await ReadBytesInternalAsync(2, 5000, ct);
                if (ack == null) return null;

                return await ReadBytesInternalAsync(expectedSize, 30000, ct);
            }
            catch { return null; }
            finally { _portLock.Release(); }
        }

        private async Task<bool> SendBinaryWriteCommandAsync(string command, byte[] data, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(command + "\n");
                _port.Write(cmdBytes, 0, cmdBytes.Length);

                var ack = await ReadBytesInternalAsync(2, 5000, ct);
                if (ack == null) return false;

                _port.Write(data, 0, data.Length);

                var status = await ReadBytesInternalAsync(2, 30000, ct);
                return status != null;
            }
            catch { return false; }
            finally { _portLock.Release(); }
        }

        public async Task<bool> ExecuteCarbonaraAsync(
            uint da1Addr, uint hashOffset, byte[] newHash,
            uint da2Addr, byte[] patchedDa2, CancellationToken ct)
        {
            _log("[MTK-XML] Executing Carbonara exploit...");
            string cmd = $"carbonara {da1Addr:X} {hashOffset:X} {da2Addr:X}";
            return await SendXmlCommandAsync(cmd, "OK", ct);
        }

        private async Task<string?> ReadResponseAsync(int timeoutMs, CancellationToken ct)
        {
            var buffer = new StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return null;
                try
                {
                    var buf = new byte[4096];
                    int read = _port.Read(buf, 0, buf.Length, 100);
                    if (read > 0)
                    {
                        buffer.Append(Encoding.ASCII.GetString(buf, 0, read));
                    }
                    else
                    {
                        await Task.Delay(10, ct);
                    }
                }
                catch { await Task.Delay(50, ct); }
            }

            return buffer.Length > 0 ? buffer.ToString() : null;
        }

        private async Task<byte[]?> ReadBytesInternalAsync(int count, int timeoutMs, CancellationToken ct)
        {
            var buffer = new byte[count];
            int totalRead = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (totalRead < count && DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return null;
                try
                {
                    int read = _port.Read(buffer, totalRead, count - totalRead, 100);
                    totalRead += read;
                    if (read > 0) await Task.Delay(1, ct);
                }
                catch { await Task.Delay(10, ct); }
            }

            return totalRead == count ? buffer : null;
        }

        public MtkUsbPort GetPort() => _port;

        public void Dispose()
        {
            IsConnected = false;
        }
    }
}