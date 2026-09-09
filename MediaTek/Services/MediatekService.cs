using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.MediaTek.Database;
using v1per_wpf.MediaTek.Exploit;
using v1per_wpf.MediaTek.Models;
using v1per_wpf.MediaTek.Protocol;

namespace v1per_wpf.MediaTek.Services
{
    public class MediatekService : IDisposable
    {
        private BromClient? _brom;
        private XmlDaClient? _xmlClient;
        private DaLoader? _daLoader;

        private string _daFilePath = "";
        private CancellationTokenSource? _connectCts;

        public event Action<string>? OnLog;
        public event Action<int>? OnProgress;
        public event Action<MtkDeviceState>? OnStateChanged;
        public event Action<string>? OnDeviceDetected;

        public bool IsConnected => _brom?.IsConnected ?? false;
        public bool IsBromMode => _brom?.IsBromMode ?? false;
        public MtkDeviceState State => _brom?.State ?? MtkDeviceState.Disconnected;
        public MtkChipInfo? ChipInfo => _brom?.ChipInfo;
        public MtkDeviceInfo? CurrentDevice { get; private set; }
        public string DaFilePath => _daFilePath;

        public void SetDaFilePath(string path)
        {
            if (File.Exists(path))
            {
                _daFilePath = path;
                MtkChipDatabase.SetDaFilePath(path);
                Log($"[MTK] DA file: {Path.GetFileName(path)}");
            }
        }

        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            _connectCts?.Cancel();
            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Log("[MTK] Scanning for MTK USB device...");
            Log("[MTK] Power off phone, hold Vol+Vol-+Power, then plug USB");
            OnStateChanged?.Invoke(MtkDeviceState.Handshaking);

            try
            {
                _brom?.Dispose();
                _brom = new BromClient(Log, msg => Log(msg));
                _daLoader = new DaLoader(_brom, Log, p => OnProgress?.Invoke((int)p));

                if (!await _brom.ConnectAsync(_connectCts.Token))
                {
                    Log("[MTK] No MTK device detected (timeout 120s)");
                    OnStateChanged?.Invoke(MtkDeviceState.Disconnected);
                    return false;
                }

                OnDeviceDetected?.Invoke(_brom.PortName);

                if (!await _brom.HandshakeAsync(50, _connectCts.Token))
                {
                    Log("[MTK] Handshake failed");
                    _brom.Disconnect();
                    OnStateChanged?.Invoke(MtkDeviceState.Disconnected);
                    return false;
                }

                if (!await _brom.InitializeAsync(_connectCts.Token))
                {
                    Log("[MTK] Init failed");
                    _brom.Disconnect();
                    OnStateChanged?.Invoke(MtkDeviceState.Disconnected);
                    return false;
                }

                CurrentDevice = new MtkDeviceInfo
                {
                    ComPort = _brom.PortName,
                    IsDownloadMode = true,
                    ChipInfo = _brom.ChipInfo,
                    MeId = _brom.MeId,
                    SocId = _brom.SocId
                };

                Log($"[MTK] Connected: {_brom.ChipInfo.GetChipName()}");
                OnStateChanged?.Invoke(_brom.State);

                // Auto-load DA if file is set
                if (!string.IsNullOrEmpty(_daFilePath) && File.Exists(_daFilePath))
                {
                    Log("[MTK] Auto-loading DA...");
                    await LoadDaAsync(_connectCts.Token);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"[MTK] Error: {ex.Message}");
                OnStateChanged?.Invoke(MtkDeviceState.Disconnected);
                return false;
            }
        }

        public void Disconnect()
        {
            _connectCts?.Cancel();
            _brom?.Disconnect();
            _xmlClient?.Dispose();
            _xmlClient = null;

            if (CurrentDevice != null)
            {
                CurrentDevice = null;
            }

            Log("[MTK] Disconnected");
            OnStateChanged?.Invoke(MtkDeviceState.Disconnected);
        }

        public async Task<bool> LoadDaAsync(CancellationToken ct = default)
        {
            if (_brom == null || !_brom.IsConnected || _brom.HwCode == 0)
            {
                Log("[MTK] Device not connected");
                return false;
            }

            ushort hwCode = _brom.HwCode;
            Log($"[MTK] Loading DA (HW: 0x{hwCode:X4})");

            DaEntry? da1 = null;
            DaEntry? da2 = null;

            if (!string.IsNullOrEmpty(_daFilePath))
            {
                var daResult = _daLoader!.ParseDaFile(_daFilePath, hwCode);
                if (daResult != null)
                {
                    da1 = daResult.Da1;
                    da2 = da2 ?? daResult.Da2;
                }
            }

            if (da1 == null)
            {
                Log("[MTK] No DA1 found");
                return false;
            }

            uint targetConfig = (uint)_brom.TargetConfig;
            if (targetConfig != 0 && _brom.IsBromMode)
            {
                Log("[MTK] Security detected, attempting BROM exploit...");
                var exploit = new BromExploit(Log);
                bool exploited = await exploit.RunExploitAsync(_brom, ct);
                if (exploited)
                {
                    Log("[MTK] Exploit successful");
                }
                else
                {
                    Log("[MTK] Exploit failed, continuing...");
                }
            }

            Log("[MTK] Uploading DA1...");
            if (!await _daLoader.UploadDa1Async(da1, ct))
            {
                Log("[MTK] DA1 upload failed");
                return false;
            }

            Log("[MTK] DA1 uploaded, waiting for DA ready...");

            _xmlClient = new XmlDaClient(
                _brom.GetPort()!,
                Log,
                msg => Log(msg),
                _brom.GetPortLock()
            );

            if (!await _xmlClient.WaitForDaReadyAsync(30000, ct))
            {
                Log("[MTK] Timeout waiting for DA1 ready");
                return false;
            }

            Log("[MTK] DA1 ready, setting runtime parameters...");
            await _xmlClient.SetRuntimeParametersAsync(ct);

            if (da2 != null)
            {
                Log("[MTK] Uploading DA2...");
                if (!await _daLoader.UploadDa2Async(da2, _xmlClient, ct))
                {
                    Log("[MTK] DA2 upload failed");
                    return false;
                }
            }

            Log("[MTK] DA loading completed");
            OnStateChanged?.Invoke(MtkDeviceState.Da2Loaded);
            return true;
        }

        public async Task<bool> RunBromExploitAsync(CancellationToken ct = default)
        {
            if (_brom == null || !_brom.IsConnected)
            {
                Log("[MTK] Device not connected");
                return false;
            }

            var exploit = new BromExploit(Log);
            return await exploit.RunExploitAsync(_brom, ct);
        }

        public async Task<List<MtkPartitionInfo>?> ReadPartitionTableAsync(CancellationToken ct = default)
        {
            if (_xmlClient == null || !_xmlClient.IsConnected)
            {
                Log("[MTK] DA not loaded");
                return null;
            }
            return await _xmlClient.ReadPartitionTableAsync(ct);
        }

        public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath, ulong size, CancellationToken ct = default)
        {
            Log($"[MTK] Reading partition: {partitionName}");

            if (_xmlClient == null || !_xmlClient.IsConnected)
            {
                Log("[MTK] DA not loaded");
                return false;
            }

            byte[]? data = await _xmlClient.ReadPartitionAsync(partitionName, size, ct);
            if (data != null && data.Length > 0)
            {
                File.WriteAllBytes(outputPath, data);
                Log($"[MTK] Partition {partitionName} saved ({data.Length} bytes)");
                return true;
            }

            Log($"[MTK] Failed to read partition {partitionName}");
            return false;
        }

        public async Task<bool> WritePartitionAsync(string partitionName, string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
            {
                Log($"[MTK] File not found: {filePath}");
                return false;
            }

            byte[] data = File.ReadAllBytes(filePath);
            Log($"[MTK] Writing partition: {partitionName} ({data.Length} bytes)");

            if (_xmlClient == null || !_xmlClient.IsConnected)
            {
                Log("[MTK] DA not loaded");
                return false;
            }

            bool success = await _xmlClient.WritePartitionAsync(partitionName, data, ct);
            if (success)
                Log($"[MTK] Partition {partitionName} written");
            else
                Log($"[MTK] Failed to write partition {partitionName}");

            return success;
        }

        public async Task<bool> ErasePartitionAsync(string partitionName, CancellationToken ct = default)
        {
            Log($"[MTK] Erasing partition: {partitionName}");

            if (_xmlClient == null || !_xmlClient.IsConnected)
            {
                Log("[MTK] DA not loaded");
                return false;
            }

            bool success = await _xmlClient.ErasePartitionAsync(partitionName, ct);
            if (success)
                Log($"[MTK] Partition {partitionName} erased");
            else
                Log($"[MTK] Failed to erase partition {partitionName}");

            return success;
        }

        public async Task<bool> FlashMultipleAsync(Dictionary<string, string> partitionFiles, CancellationToken ct = default)
        {
            if (_xmlClient == null || !_xmlClient.IsConnected)
            {
                Log("[MTK] DA not loaded");
                return false;
            }

            Log($"[MTK] Flashing {partitionFiles.Count} partitions...");
            int success = 0;
            int total = partitionFiles.Count;

            foreach (var kvp in partitionFiles)
            {
                if (ct.IsCancellationRequested) break;
                if (await WritePartitionAsync(kvp.Key, kvp.Value, ct)) success++;
                OnProgress?.Invoke((int)((double)success / total * 100));
            }

            Log($"[MTK] Flash completed: {success}/{total}");
            return success == total;
        }

        public async Task<bool> RebootAsync(CancellationToken ct = default)
        {
            if (_xmlClient == null || !_xmlClient.IsConnected) return false;
            return await _xmlClient.RebootAsync(ct);
        }

        public MtkSecurityInfo? GetSecurityInfo()
        {
            if (_brom == null) return null;

            return new MtkSecurityInfo
            {
                SecureBootEnabled = _brom.TargetConfig.HasFlag(TargetConfigFlags.SbcEnabled),
                SlaEnabled = _brom.TargetConfig.HasFlag(TargetConfigFlags.SlaEnabled),
                DaaEnabled = _brom.TargetConfig.HasFlag(TargetConfigFlags.DaaEnabled),
                MeId = _brom.MeId != null ? BitConverter.ToString(_brom.MeId).Replace("-", "") : "",
                SocId = _brom.SocId != null ? BitConverter.ToString(_brom.SocId).Replace("-", "") : "",
                SbcEnabled = _brom.TargetConfig.HasFlag(TargetConfigFlags.SbcEnabled)
            };
        }

        public MtkExploitInfo GetExploitInfo()
        {
            ushort hwCode = _brom?.HwCode ?? 0;
            string exploitType = MtkChipDatabase.GetExploitType(hwCode);

            return new MtkExploitInfo
            {
                IsConnected = IsConnected,
                ChipName = BromExploit.GetChipName(hwCode),
                HwCode = hwCode,
                ExploitType = string.IsNullOrEmpty(exploitType) ? "None" : exploitType,
                IsCarbonaraSupported = CarbonaraExploit.QuickCheck(hwCode),
                IsAllinoneSignatureSupported = exploitType == "AllinoneSignature"
            };
        }

        private void Log(string message) => OnLog?.Invoke(message);

        public void Dispose()
        {
            _brom?.Dispose();
            _xmlClient?.Dispose();
        }
    }
}