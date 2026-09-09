using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.MediaTek.Common;
using v1per_wpf.MediaTek.Database;
using v1per_wpf.MediaTek.Models;

namespace v1per_wpf.MediaTek.Protocol
{
    public class BromClient : IDisposable
    {
        private MtkUsbPort? _usbPort;
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;
        private readonly SemaphoreSlim _portLock = new(1, 1);

        private const int DefaultTimeoutMs = 3000;

        public bool IsConnected { get; private set; }
        public bool IsBromMode { get; private set; }
        public MtkDeviceState State { get; internal set; }
        public ushort LastUploadStatus { get; private set; }

        public ushort HwCode { get; private set; }
        public ushort HwVer { get; private set; }
        public ushort HwSubCode { get; private set; }
        public ushort SwVer { get; private set; }
        public byte BromVer { get; private set; }
        public byte BlVer { get; private set; }
        public byte[]? MeId { get; private set; }
        public byte[]? SocId { get; private set; }
        public TargetConfigFlags TargetConfig { get; private set; }
        public MtkChipInfo ChipInfo { get; private set; } = new();

        public BromClient(Action<string> log, Action<string>? logDetail = null)
        {
            _log = log;
            _logDetail = logDetail ?? log;
            State = MtkDeviceState.Disconnected;
        }

        public MtkUsbPort? GetPort() => _usbPort;
        public SemaphoreSlim GetPortLock() => _portLock;
        public string PortName => _usbPort?.PortName ?? "";
        public bool IsPortOpen => _usbPort?.IsOpen ?? false;

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                _usbPort?.Dispose();

                _log("[MTK] Scanning for MTK USB device...");
                _usbPort = await MtkUsbPort.FindAndOpenAsync(ct);

                if (_usbPort == null)
                {
                    _log("[MTK] No MTK device found");
                    return false;
                }

                IsConnected = true;
                State = MtkDeviceState.Handshaking;
                _log($"[MTK] Device found: {_usbPort.PortName}");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[MTK] Connection failed: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            _usbPort?.Close();
            IsConnected = false;
            State = MtkDeviceState.Disconnected;
        }

        public async Task<bool> HandshakeAsync(int maxTries = 50, CancellationToken ct = default)
        {
            if (!IsConnected || _usbPort == null) return false;

            _log("[MTK] Starting handshake...");
            State = MtkDeviceState.Handshaking;

            byte[] seq = { 0xA0, 0x0A, 0x50, 0x05 };

            for (int attempt = 0; attempt < maxTries; attempt++)
            {
                if (ct.IsCancellationRequested) return false;

                try
                {
                    _usbPort.DiscardBuffers();

                    bool ok = true;
                    foreach (byte b in seq)
                    {
                        _usbPort.Write(new[] { b }, 0, 1);
                        await Task.Delay(5, ct);

                        byte resp = _usbPort.ReadByte(2000);
                        byte expected = (byte)(b ^ 0xFF);

                        if (resp == expected)
                        {
                            continue;
                        }
                        else if (resp == seq[0])
                        {
                            _log("[MTK] Handshake complete (echo mode)");
                            _usbPort.DiscardBuffers();
                            return true;
                        }
                        else
                        {
                            ok = false;
                            break;
                        }
                    }

                    if (ok)
                    {
                        _log("[MTK] Handshake successful");
                        _usbPort.DiscardBuffers();
                        return true;
                    }

                    if (attempt % 10 == 0 && attempt > 0)
                        _logDetail($"[MTK] Retrying handshake ({attempt}/{maxTries})");

                    await Task.Delay(50, ct);
                }
                catch (Exception ex)
                {
                    _logDetail($"[MTK] Handshake error: {ex.Message}");
                    await Task.Delay(50, ct);
                }
            }

            _log("[MTK] Handshake failed");
            State = MtkDeviceState.Error;
            return false;
        }

        public async Task<bool> InitializeAsync(CancellationToken ct = default)
        {
            if (State == MtkDeviceState.Disconnected || State == MtkDeviceState.Error) return false;

            try
            {
                // Send sync to clear stale data
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        _usbPort!.Write(new byte[] { 0xA0 }, 0, 1);
                        await Task.Delay(5, ct);
                        if (_usbPort != null) _usbPort.DiscardBuffers();
                    }
                    catch { }
                }

                // Get HW code
                var hwInfo = await GetHwCodeAsync(ct);
                if (hwInfo != null)
                {
                    HwCode = hwInfo.Value.hwCode;
                    HwVer = hwInfo.Value.hwVer;
                    _log($"[MTK] HW Code: 0x{HwCode:X4}");

                    var chipRecord = MtkChipDatabase.GetChip(HwCode);
                    if (chipRecord != null)
                    {
                        ChipInfo = MtkChipDatabase.ToChipInfo(chipRecord);
                        ChipInfo.HwVer = HwVer;
                        _log($"[MTK] CPU: {ChipInfo.ChipName}");
                    }
                    else
                    {
                        ChipInfo.HwCode = HwCode;
                        ChipInfo.HwVer = HwVer;
                        ChipInfo.WatchdogAddr = 0x10007000;
                        ChipInfo.DaPayloadAddr = 0x200000;
                        _log($"[MTK] Unknown chip: 0x{HwCode:X4}");
                    }
                }

                // Get target config
                var config = await GetTargetConfigAsync(ct);
                if (config != null)
                {
                    TargetConfig = config.Value;
                    _log($"[MTK] Target Config: 0x{(uint)TargetConfig:X8}");
                }

                // Get BL version to determine mode
                BlVer = await GetBlVerAsync(ct);
                IsBromMode = (BlVer == 0xFF);

                if (IsBromMode)
                {
                    _log("[MTK] Mode: BROM");
                    State = MtkDeviceState.Brom;
                }
                else
                {
                    _log($"[MTK] Mode: Preloader (BL Ver: {BlVer})");
                    State = MtkDeviceState.Preloader;
                }

                // Get IDs
                MeId = await GetMeIdAsync(ct);
                SocId = await GetSocIdAsync(ct);

                return true;
            }
            catch (Exception ex)
            {
                _log($"[MTK] Init failed: {ex.Message}");
                return false;
            }
        }

        public async Task<(ushort hwCode, ushort hwVer)?> GetHwCodeAsync(CancellationToken ct = default)
        {
            if (!await EchoAsync(BromCommands.CMD_GET_HW_CODE, ct)) return null;
            var response = await ReadBytesAsync(4, DefaultTimeoutMs, ct);
            if (response == null || response.Length < 4) return null;
            return (MtkDataPacker.UnpackUInt16BE(response, 0), MtkDataPacker.UnpackUInt16BE(response, 2));
        }

        public async Task<TargetConfigFlags?> GetTargetConfigAsync(CancellationToken ct = default)
        {
            if (!await EchoAsync(BromCommands.CMD_GET_TARGET_CONFIG, ct)) return null;
            var response = await ReadBytesAsync(6, DefaultTimeoutMs, ct);
            if (response == null || response.Length < 6) return null;
            uint config = MtkDataPacker.UnpackUInt32BE(response, 0);
            return (TargetConfigFlags)config;
        }

        public async Task<byte> GetBlVerAsync(CancellationToken ct = default)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                _usbPort!.Write(new byte[] { BromCommands.CMD_GET_BL_VER }, 0, 1);
                await Task.Delay(5, ct);
                return _usbPort!.ReadByte(2000);
            }
            finally { _portLock.Release(); }
        }

        public async Task<byte> GetBromVerAsync(CancellationToken ct = default)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                _usbPort!.Write(new byte[] { BromCommands.CMD_GET_VERSION }, 0, 1);
                await Task.Delay(5, ct);
                return _usbPort!.ReadByte(2000);
            }
            finally { _portLock.Release(); }
        }

        public async Task<byte[]?> GetMeIdAsync(CancellationToken ct = default)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                // Send GET_BL_VER first
                _usbPort!.Write(new byte[] { BromCommands.CMD_GET_BL_VER }, 0, 1);
                await Task.Delay(5, ct);
                _usbPort!.ReadByte(2000);

                // Now get MEID
                _usbPort.Write(new byte[] { BromCommands.CMD_GET_ME_ID }, 0, 1);
                await Task.Delay(5, ct);

                byte cmdResp = _usbPort.ReadByte(2000);
                if (cmdResp != BromCommands.CMD_GET_ME_ID) return null;

                var lenResp = await ReadBytesInternalAsync(4, DefaultTimeoutMs, ct);
                if (lenResp == null) return null;

                uint length = MtkDataPacker.UnpackUInt32BE(lenResp, 0);
                if (length == 0 || length > 64) return null;

                var meId = await ReadBytesInternalAsync((int)length, DefaultTimeoutMs, ct);
                var statusResp = await ReadBytesInternalAsync(2, DefaultTimeoutMs, ct);
                if (statusResp == null) return null;

                ushort status = MtkDataPacker.UnpackUInt16LE(statusResp, 0);
                return status == 0 ? meId : null;
            }
            finally { _portLock.Release(); }
        }

        public async Task<byte[]?> GetSocIdAsync(CancellationToken ct = default)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                _usbPort!.Write(new byte[] { BromCommands.CMD_GET_BL_VER }, 0, 1);
                await Task.Delay(5, ct);
                _usbPort!.ReadByte(2000);

                _usbPort.Write(new byte[] { BromCommands.CMD_GET_SOC_ID }, 0, 1);
                await Task.Delay(5, ct);

                byte cmdResp = _usbPort.ReadByte(2000);
                if (cmdResp != BromCommands.CMD_GET_SOC_ID) return null;

                var lenResp = await ReadBytesInternalAsync(4, DefaultTimeoutMs, ct);
                if (lenResp == null) return null;

                uint length = MtkDataPacker.UnpackUInt32BE(lenResp, 0);
                if (length == 0 || length > 64) return null;

                var socId = await ReadBytesInternalAsync((int)length, DefaultTimeoutMs, ct);
                var statusResp = await ReadBytesInternalAsync(2, DefaultTimeoutMs, ct);
                if (statusResp == null) return null;

                ushort status = MtkDataPacker.UnpackUInt16LE(statusResp, 0);
                return status == 0 ? socId : null;
            }
            finally { _portLock.Release(); }
        }

        public async Task<bool> SendDaAsync(uint address, byte[] data, int sigLen = 0, CancellationToken ct = default)
        {
            try
            {
                _log($"[MTK] Sending DA to 0x{address:X8}, size {data.Length} bytes");

                byte[] dataWithoutSig = data;
                byte[]? signature = null;
                if (sigLen > 0 && data.Length >= sigLen)
                {
                    dataWithoutSig = new byte[data.Length - sigLen];
                    Array.Copy(data, 0, dataWithoutSig, 0, data.Length - sigLen);
                    signature = new byte[sigLen];
                    Array.Copy(data, data.Length - sigLen, signature, 0, sigLen);
                }

                var (checksum, processedData) = MtkChecksum.PrepareData(dataWithoutSig, signature, dataWithoutSig.Length);

                _usbPort!.Write(new byte[] { BromCommands.CMD_SEND_DA }, 0, 1);
                var cmdResp = await ReadBytesAsync(1, DefaultTimeoutMs, ct);
                if (cmdResp == null || cmdResp.Length == 0) return false;

                if (!await EchoAsync(MtkDataPacker.PackUInt32BE(address), ct)) return false;
                if (!await EchoAsync(MtkDataPacker.PackUInt32BE((uint)processedData.Length), ct)) return false;
                if (!await EchoAsync(MtkDataPacker.PackUInt32BE((uint)sigLen), ct)) return false;

                var statusResp = await ReadBytesAsync(2, DefaultTimeoutMs, ct);
                if (statusResp == null) return false;

                ushort status = MtkDataPacker.UnpackUInt16BE(statusResp, 0);
                LastUploadStatus = status;

                if (status == (ushort)BromStatus.AuthRequired || status == (ushort)BromStatus.PreloaderAuth)
                {
                    _log("[MTK] Preloader mode requires AUTH");
                    return false;
                }

                if (!BromErrorHelper.IsSuccess(status))
                {
                    _log($"[MTK] SEND_DA error: {BromErrorHelper.GetErrorMessage(status)}");
                    return false;
                }

                // Send data in chunks
                await _portLock.WaitAsync(ct);
                try
                {
                    int chunkSize = 0x400;
                    int pos = 0;
                    while (pos < processedData.Length)
                    {
                        int size = Math.Min(processedData.Length - pos, chunkSize);
                        _usbPort.Write(processedData, pos, size);
                        pos += size;
                    }
                }
                finally { _portLock.Release(); }

                await Task.Delay(10, ct);
                var checksumResp = await ReadBytesAsync(2, 2000, ct);
                var finalStatus = await ReadBytesAsync(2, 2000, ct);

                _log("[MTK] DA sent successfully");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[MTK] SendDa exception: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> JumpDaAsync(uint address, CancellationToken ct = default)
        {
            _log($"[MTK] Jumping to DA at 0x{address:X8}");

            if (!await EchoAsync(BromCommands.CMD_JUMP_DA, ct)) return false;

            await _portLock.WaitAsync(ct);
            try { _usbPort!.Write(MtkDataPacker.PackUInt32BE(address), 0, 4); }
            finally { _portLock.Release(); }

            var addrResp = await ReadBytesAsync(4, DefaultTimeoutMs, ct);
            var statusResp = await ReadBytesAsync(2, DefaultTimeoutMs, ct);

            return statusResp != null;
        }

        public async Task<bool> SendExploitPayloadAsync(byte[] payload, CancellationToken ct = default)
        {
            try
            {
                _log($"[MTK] Sending exploit payload ({payload.Length} bytes)");

                if (!await EchoAsync(BromCommands.CMD_SEND_CERT, ct)) return false;
                if (!await EchoAsync(MtkDataPacker.PackUInt32BE((uint)payload.Length), ct)) return false;

                var statusResp = await ReadBytesAsync(2, DefaultTimeoutMs, ct);
                if (statusResp == null) return false;

                ushort status = MtkDataPacker.UnpackUInt16BE(statusResp, 0);
                if (status > 0xFF)
                {
                    _log($"[MTK] SEND_CERT rejected: {BromErrorHelper.GetErrorMessage(status)}");
                    return false;
                }

                await _portLock.WaitAsync(ct);
                try
                {
                    int chunkSize = 0x400;
                    int pos = 0;
                    while (pos < payload.Length)
                    {
                        int size = Math.Min(payload.Length - pos, chunkSize);
                        _usbPort!.Write(payload, pos, size);
                        pos += size;
                    }
                }
                finally { _portLock.Release(); }

                await Task.Delay(10, ct);
                await ReadBytesAsync(2, 2000, ct);
                var finalResp = await ReadBytesAsync(2, 2000, ct);

                _log("[MTK] Exploit payload uploaded");
                return true;
            }
            catch (Exception ex)
            {
                _log($"[MTK] Exploit payload exception: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> EchoAsync(byte cmd, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                _usbPort!.Write(new[] { cmd }, 0, 1);
                await Task.Delay(5, ct);
                byte resp = _usbPort!.ReadByte(2000);
                return resp == cmd;
            }
            finally { _portLock.Release(); }
        }

        private async Task<bool> EchoAsync(byte[] data, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try
            {
                _usbPort!.Write(data, 0, data.Length);
                await Task.Delay(5, ct);
                for (int i = 0; i < data.Length; i++)
                {
                    byte resp = _usbPort!.ReadByte(2000);
                    if (resp != data[i]) return false;
                }
                return true;
            }
            finally { _portLock.Release(); }
        }

        private async Task<byte[]?> ReadBytesAsync(int count, int timeoutMs, CancellationToken ct)
        {
            await _portLock.WaitAsync(ct);
            try { return await ReadBytesInternalAsync(count, timeoutMs, ct); }
            finally { _portLock.Release(); }
        }

        private async Task<byte[]?> ReadBytesInternalAsync(int count, int timeoutMs, CancellationToken ct)
        {
            if (_usbPort == null || !_usbPort.IsOpen) return null;

            var buffer = new byte[count];
            int totalRead = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (totalRead < count && DateTime.UtcNow < deadline)
            {
                if (ct.IsCancellationRequested) return null;
                try
                {
                    int read = _usbPort.Read(buffer, totalRead, count - totalRead, 100);
                    totalRead += read;
                    if (read > 0) await Task.Delay(1, ct);
                }
                catch { break; }
            }

            return totalRead == count ? buffer : (totalRead > 0 ? buffer[..totalRead] : null);
        }

        public void Dispose()
        {
            Disconnect();
            _usbPort?.Dispose();
            _portLock.Dispose();
        }
    }
}