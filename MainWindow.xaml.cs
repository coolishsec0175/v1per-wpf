using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Spectre.Console;
using V1Per.Qualcomm.Services;
using V1Per.Qualcomm.Authentication;
using V1Per.Qualcomm.Exploit;
using v1per_wpf.Qualcomm;
using v1per_wpf.MediaTek.Services;
using v1per_wpf.MediaTek.Models;

namespace v1per_wpf;

using V1Per;

public partial class MainWindow : Window
{
    private readonly AnsiRenderer _renderer;
    private readonly RendererWriter _writer;
    private readonly StringBuilder _reveal = new();
    private bool _busy;
    private readonly ObservableCollection<PartitionRow> _partitions = new();
    private readonly MediatekService _mtkService = new();
    private readonly ObservableCollection<MtkPartitionRow> _mtkPartitions = new();

    public MainWindow()
    {
        InitializeComponent();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject?.ToString());
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception?.ToString());
            e.Handled = true;
        };

        _renderer = new AnsiRenderer(ConsoleOutput);
        _writer = new RendererWriter();
        var output = new AnsiConsoleOutput(_writer);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = output,
        });

        var drain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        drain.Tick += (_, _) => _writer.Drain();
        drain.Start();

        var reveal = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(18) };
        reveal.Tick += (_, _) => RevealStep();
        reveal.Start();

        Dialogs.Init(this);
        PartitionGrid.ItemsSource = _partitions;

        Unisoc.Progress = pct => Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressBar2.Value = Math.Clamp(pct, 0, 100);
            if (pct >= 100)
            {
                ProgressBar2.Value = 0;
                ProgressBar2.IsIndeterminate = false;
            }
            else
            {
                ProgressBar2.IsIndeterminate = false;
            }
        }));

        QcFlash.Progress = f => Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressBar2.Value = Math.Clamp((int)(f * 100), 0, 100);
        }));

        Loaded += (_, _) =>
        {
            PopulateChipsets();
            PopulateDeviceList();
            RefreshPorts();
            RefreshEdlPorts();
            MtkPartitionGrid.ItemsSource = _mtkPartitions;
            V1Per.Ui.Banner();
            V1Per.Ui.Muted("Pick a tab and run tools. Console shows live output.");
            AnsiConsole.WriteLine();
        };

        _mtkService.OnLog += msg => Dispatcher.BeginInvoke(new Action(() => V1Per.Ui.Muted(msg)));
        _mtkService.OnProgress += pct => Dispatcher.BeginInvoke(new Action(() =>
        {
            ProgressBar2.Value = Math.Clamp(pct, 0, 100);
        }));
        _mtkService.OnStateChanged += state => Dispatcher.BeginInvoke(new Action(() =>
        {
            bool connected = state != MtkDeviceState.Disconnected && state != MtkDeviceState.Error;
            BtnMtkConnect.IsEnabled = !connected;
            BtnMtkDisconnect.IsEnabled = connected;
            BtnMtkReadInfo.IsEnabled = connected;
            BtnMtkReboot.IsEnabled = connected;
            BtnMtkFlash.IsEnabled = connected;
            BtnMtkReadPart.IsEnabled = connected;
            BtnMtkErase.IsEnabled = connected;
            BtnMtkExploitCheck.IsEnabled = connected;
            BtnMtkBromExploit.IsEnabled = connected && TglMtkExploitEnable.IsChecked == true;
        }));
    }

    // -- console reveal ---------------------------------------------

    private void RevealStep()
    {
        while (_reveal.Length == 0 && _writer.TryDequeue(out string? text))
        {
            if (!string.IsNullOrEmpty(text))
                _reveal.Append(text);
        }
        if (_reveal.Length == 0)
            return;

        int rate = _reveal.Length > 3000 ? 18 : _reveal.Length > 800 ? 6 : 2;
        int cut = Math.Min(rate, _reveal.Length);
        string pending = _reveal.ToString();

        int esc = pending.IndexOf('\x1b');
        if (esc >= 0 && esc < cut)
        {
            int end = FindEscapeEnd(pending, esc);
            cut = end < 0 ? esc : Math.Min(end, pending.Length);
        }

        string slice = pending[..cut];
        _reveal.Remove(0, cut);
        _renderer.Feed(slice);
    }

    private static int FindEscapeEnd(string s, int start)
    {
        int i = start + 1;
        if (i >= s.Length)
            return -1;
        if (s[i] == '[')
        {
            for (i++; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 0x40 && c <= 0x7E)
                    return i + 1;
            }
            return -1;
        }
        if (s[i] == ']')
        {
            for (i++; i < s.Length; i++)
            {
                if (s[i] == '\x07')
                    return i + 1;
                if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '\\')
                    return i + 2;
            }
            return -1;
        }
        return i + 1;
    }

    // -- command runner ---------------------------------------------

    private void RunAsync(Func<Task> work)
    {
        if (_busy)
        {
            V1Per.Ui.Warn("An operation is already running.");
            return;
        }
        _busy = true;
        SetBusy(true);
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                V1Per.Ui.Err("Operation failed:");
                V1Per.Ui.Muted(Markup.Escape(ex.Message));
            }
            finally
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    _busy = false;
                    SetBusy(false);
                }));
            }
        });
    }

    private void SetDeviceInfo(string text)
    {
        Dispatcher.BeginInvoke(new Action(() => TxtDeviceInfo.Text = text));
    }

    private void AppendDeviceInfo(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (string.IsNullOrEmpty(TxtDeviceInfo.Text))
                TxtDeviceInfo.Text = text;
            else
                TxtDeviceInfo.Text += "\n" + text;
        }));
    }

    private void RunCommand(string input) => RunAsync(() => RunCommandAsync(input));

    private static async Task RunCommandAsync(string input)
    {
        string[] bits = Tokenize(input);
        string cmd = bits[0].ToLowerInvariant();
        string[] rest = bits.Skip(1).ToArray();

        switch (cmd)
        {
            case "force": V1Per.ForceFastboot.Run(); break;
            case "devices": Commands.Devices(); break;
            case "root": await Root.Run(rest); break;
            case "scatter": await Scatter.Run(rest); break;
            case "anykernel": await AnyKernel.Run(rest); break;
            case "drivers": await Drivers.Run(rest); break;
            case "scrcpy": Commands.Scrcpy(rest); break;
            case "demo": V1Per.Showcase.Show(); break;
            case "unisoc": await Unisoc.Run(rest); break;
            case "qualcomm": case "qcom": case "edl": await QcFlash.Run(rest); break;
            default: V1Per.Ui.Err($"Unknown command: '{cmd}'"); break;
        }
    }

    private static string[] Tokenize(string input)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in input)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens.ToArray();
    }

    private void SetBusy(bool busy)
    {
        ProgressBar1.IsIndeterminate = busy;
        BtnStop.IsEnabled = busy;
        foreach (var button in Buttons())
            button.IsEnabled = !busy;
    }

    private IEnumerable<Button> Buttons()
    {
        yield return BtnIdentify;
        yield return BtnReadPartition;
        yield return BtnFlashPartition;
        yield return BtnErase;
        yield return BtnEraseFrp;
        yield return BtnLoadScatter;
        yield return BtnUnlock;
        yield return BtnDump;
        yield return BtnUnisocFlash;
        yield return BtnUnisocRoot;
        yield return BtnEdlRefresh;
        yield return BtnEdlDetect;
        yield return BtnQcFlash;
        yield return BtnQcDump;
        yield return BtnForceFastboot;
        yield return BtnDevices;
        yield return BtnRoot;
        yield return BtnScatter;
        yield return BtnAnyKernel;
        yield return BtnDrivers;
        yield return BtnScrcpy;
        yield return BtnDemo;
    }

    // -- title bar / theme ------------------------------------------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && WindowState == WindowState.Normal)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (ThemeToggle is null)
            return;
        if (Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/") == true) is { } dict)
        {
            string target = ThemeToggle.IsChecked == true ? "Themes/Dark.xaml" : "Themes/Light.xaml";
            dict.Source = new Uri(target, UriKind.Relative);
        }
    }

    private void ConsoleOutput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ConsoleOutput.ScrollToEnd();
    }

    private static void LogCrash(string? detail)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "v1per_crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {detail}\n\n");
        }
        catch (Exception) { }
    }

    // -- connection bar ---------------------------------------------

    private void BtnRefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        ComboPort.Items.Clear();
        foreach (var port in ComPorts.Enumerate())
            ComboPort.Items.Add($"{port.Device}  �  {port.Description}");
        if (ComboPort.Items.Count > 0)
            ComboPort.SelectedIndex = 0;
    }

    private void BtnInstallDriver_Click(object sender, RoutedEventArgs e) =>
        RunCommand("drivers");

    private void BtnDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            V1Per.Ui.Err($"Failed to open Device Manager: {ex.Message}");
        }
    }

    // -- Flash tab --------------------------------------------------

    private void BtnIdentify_Click(object sender, RoutedEventArgs e) =>
        RunCommand(RdDownload.IsChecked == true ? "unisoc parts" : "devices");

    private void BtnReadPartition_Click(object sender, RoutedEventArgs e)
    {
        var selected = _partitions.Where(p => p.IsChecked).ToList();
        if (selected.Count == 0)
        {
            V1Per.Ui.Warn("Check at least one partition row to read.");
            V1Per.Ui.Muted("Hint: run Identify first to pull the on-device GPT partition table.");
            return;
        }
        var names = selected.Select(p => p.Name).Distinct().ToList();
        RunCommand($"unisoc read {string.Join(" ", names.Select(n => "\"" + n + "\""))}");
    }

    private void SyncFdlOverrides()
    {
        Unisoc.OverrideFdl1 = TxtFDL1.Text.Trim();
        Unisoc.OverrideFdl1Addr = TxtFDL1Addr.Text.Trim();
        Unisoc.OverrideFdl2 = TxtFDL2.Text.Trim();
        Unisoc.OverrideFdl2Addr = TxtFDL2Addr.Text.Trim();
        Unisoc.KeepNv = CkKeepNv.IsChecked == true;
    }

    private void BtnFlashPartition_Click(object sender, RoutedEventArgs e)
    {
        SyncFdlOverrides();
        var selected = _partitions.Where(p => p.IsChecked && File.Exists(p.Path)).ToList();
        if (Unisoc.KeepNv)
            selected = selected.Where(p => !Unisoc.IsNvProtected(p.Name)).ToList();
        if (selected.Count == 0)
        {
            V1Per.Ui.Warn("No checked partition row with an existing image path.");
            V1Per.Ui.Muted("Load a scatter, check rows and set paths (double-click the Path cell).");
            return;
        }
        RunAsync(async () =>
        {
            foreach (var row in selected)
            {
                V1Per.Ui.Info($"Flashing {row.Name} ? {Path.GetFileName(row.Path)}");
                await Unisoc.Run(new[] { "flash", row.Name, row.Path });
            }
        });
    }

    private void BtnErase_Click(object sender, RoutedEventArgs e)
    {
        SyncFdlOverrides();
        var selected = _partitions.Where(p => p.IsChecked).ToList();
        if (Unisoc.KeepNv)
            selected = selected.Where(p => !Unisoc.IsNvProtected(p.Name)).ToList();
        if (selected.Count == 0)
        {
            V1Per.Ui.Warn("Check at least one partition to erase.");
            return;
        }
        RunAsync(async () =>
        {
            foreach (var row in selected)
            {
                V1Per.Ui.Warn($"Erasing {row.Name}...");
                await Unisoc.Run(new[] { "erase", row.Name });
            }
        });
    }

    private void BtnEraseFrp_Click(object sender, RoutedEventArgs e)
    {
        SyncFdlOverrides();
        RunCommand("unisoc erasefrp");
    }

    private void BtnLoadScatter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select an MTK scatter file",
            Filter = "Scatter files (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true)
            return;
        LoadScatter(dlg.FileName);
    }

    private void LoadScatter(string scatterPath)
    {
        var parts = Scatter.Parse(scatterPath);
        if (parts.Count == 0)
            return;
        _partitions.Clear();
        foreach (var part in parts)
        {
            _partitions.Add(new PartitionRow
            {
                IsChecked = part.IsDownloadable && !part.Unsafe,
                Name = part.Name,
                FileName = part.FileName,
                Size = part.SizeHuman(),
                Path = part.ResolvedPath ?? string.Empty,
            });
        }
        V1Per.Ui.Ok($"Loaded {parts.Count} partitions from scatter.");
    }

    private void BtnFDL1_Click(object sender, RoutedEventArgs e) => BrowseFdl(TxtFDL1, TxtFDL1Addr, "fdl1");
    private void BtnFDL2_Click(object sender, RoutedEventArgs e) => BrowseFdl(TxtFDL2, TxtFDL2Addr, "fdl2");
    private void BtnPacFirmware_Click(object sender, RoutedEventArgs e) => BrowseFile(TxtPacFirmware, "PAC");

    private void BrowseFdl(TextBox box, TextBox addrBox, string prefix)
    {
        var dlg = new OpenFileDialog { Title = $"Select {prefix} file", Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true)
            return;
        box.Text = dlg.FileName;

        string? dir = Path.GetDirectoryName(dlg.FileName);
        if (dir is null)
            return;
        foreach (string candidate in new[] { $"{prefix}-addr.txt", $"{prefix}_addr.txt", $"{prefix}addr.txt" })
        {
            string path = Path.Combine(dir, candidate);
            if (!File.Exists(path))
                continue;
            string text = File.ReadAllText(path).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(text, @"0x[0-9a-fA-F]+");
            if (m.Success)
            {
                addrBox.Text = m.Value;
                V1Per.Ui.Ok($"Auto-detected {prefix} address: {m.Value}");
            }
            break;
        }
    }

    private void BrowseFile(TextBox box, string label)
    {
        var dlg = new OpenFileDialog { Title = $"Select {label} file", Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true)
            box.Text = dlg.FileName;
    }

    // -- Unlock Tool tab --------------------------------------------

    private void PopulateChipsets()
    {
        ComboChipset.Items.Clear();
        foreach (var pkg in Unisoc.Packages.Values)
            ComboChipset.Items.Add($"{pkg.Id} � {pkg.Name}");
        if (ComboChipset.Items.Count > 0)
            ComboChipset.SelectedIndex = 0;
    }

    private void PopulateDeviceList()
    {
        DeviceList.Items.Clear();
        foreach (var alias in Unisoc.DeviceAliases.OrderBy(kv => kv.Value))
            DeviceList.Items.Add($"{alias.Key}  ?  {alias.Value}");
    }

    private void TxtSearchDevice_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtSearchDevice.Text == "Type for search...")
            TxtSearchDevice.Text = "";
    }

    private void TxtSearchDevice_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TxtSearchDevice.Text.Length == 0)
            TxtSearchDevice.Text = "Type for search...";
    }

    private void TxtSearchDevice_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtSearchDevice is null || DeviceList is null)
            return;
        string filter = TxtSearchDevice.Text.Trim().ToLowerInvariant();
        DeviceList.Items.Clear();
        foreach (var alias in Unisoc.DeviceAliases.OrderBy(kv => kv.Value))
        {
            if (filter.Length == 0 || filter == "type for search..."
                || alias.Key.Contains(filter) || alias.Value.Contains(filter))
                DeviceList.Items.Add($"{alias.Key}  ?  {alias.Value}");
        }
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not string item)
            return;
        var parts = item.Split("?", StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && parts[0].Length > 0)
        {
            for (int i = 0; i < ComboChipset.Items.Count; i++)
            {
                if (ComboChipset.Items[i].ToString()?.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase) == true)
                {
                    ComboChipset.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private string SelectedChipset()
    {
        if (ComboChipset.SelectedItem is string s)
            return s.Split('�')[0].Trim();
        return "ums9230";
    }

    private void BtnUnlock_Click(object sender, RoutedEventArgs e) =>
        RunCommand($"unisoc unlock {SelectedChipset()}");

    private void BtnDump_Click(object sender, RoutedEventArgs e) =>
        RunCommand($"unisoc dump {SelectedChipset()}");

    private void BtnUnisocFlash_Click(object sender, RoutedEventArgs e) =>
        RunAsync(async () =>
        {
            string partition = await Dialogs.PromptAsync("Partition name (e.g. boot):");
            if (partition.Length == 0)
                return;
            string image = await Dialogs.PromptAsync("Image file path:");
            if (image.Length == 0)
                return;
            await Unisoc.Run(new[] { "flash", SelectedChipset(), partition, image });
        });

    private void BtnUnisocRoot_Click(object sender, RoutedEventArgs e)
    {
        string boot = TxtBootImage.Text.Trim().Trim('"', '\'');
        RunCommand($"unisoc root {SelectedChipset()} {(boot.Length > 0 ? "\"" + boot + "\"" : "")}");
    }

    private void BtnBrowseBoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select boot image", Filter = "Boot images (*.img)|*.img|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true)
            TxtBootImage.Text = dlg.FileName;
    }

    // -- Qualcomm tab -----------------------------------------------

    private void RefreshEdlPorts()
    {
        ComboEdlPort.Items.Clear();
        foreach (string p in Qualcomm.ComPortCtrl.getDevicesQc())
            ComboEdlPort.Items.Add(p);
        if (ComboEdlPort.Items.Count > 0)
            ComboEdlPort.SelectedIndex = 0;
    }

    private void BtnEdlRefresh_Click(object sender, RoutedEventArgs e) => RefreshEdlPorts();

    private void BtnEdlDetect_Click(object sender, RoutedEventArgs e)
    {
        RefreshEdlPorts();
        QcFlash.Detect();
    }

    private void BtnQcBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Select a rawprogram*.xml in the firmware folder", Filter = "rawprogram xml (*.xml)|*.xml" };
        if (dlg.ShowDialog(this) != true)
            return;
        TxtQcFirmware.Text = Path.GetDirectoryName(dlg.FileName) ?? dlg.FileName;
    }

    private void BtnQcFlash_Click(object sender, RoutedEventArgs e) => RunQcOp("flash");

    private void BtnQcDump_Click(object sender, RoutedEventArgs e) => RunQcOp("dump");

    private void BtnQcCloudLoader_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        RunAsync(async () => await QcFlash.Run(new[] { "cloud", port }));
    }

    private void BtnQcStorage_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        RunAsync(async () => await QcFlash.Run(new[] { "storage", port }));
    }

    private void BtnQcReadInfo_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        SetDeviceInfo("Reading device info...");
        RunAsync(async () =>
        {
            Comm comm = null;
            SerialPort serialPort = null;
            try
            {
                comm = new Comm();
                serialPort = new SerialPort(port, 9600);
                comm.serialPort = serialPort;
                serialPort.Open();
                comm.StartReading();

                SetDeviceInfo($"Port: {port}\nWaiting for Sahara hello...");

                int retries = 10;
                while (retries-- > 0)
                {
                    comm.getRecDataIgnoreExcep();
                    if (comm.recData != null && comm.recData.Length >= 4)
                    {
                        uint cmd = BitConverter.ToUInt32(comm.recData, 0);
                        if (cmd == 1)
                        {
                            uint mode = BitConverter.ToUInt32(comm.recData, 20);
                            uint version = BitConverter.ToUInt32(comm.recData, 8);
                            AppendDeviceInfo($"Sahara v{version}, mode={mode}");
                            break;
                        }
                    }
                    await Task.Delay(500);
                }

                AppendDeviceInfo("Sending firehose configure...");
                string configure = string.Format(v1per_wpf.Qualcomm.Firehose.Configure, "1", 4096 * 256, "ufs", 0);
                comm.SendCommand(configure, checkAck: true);
                comm.GetResponse(waiteACK: true);

                var sb = new StringBuilder();
                sb.AppendLine($"Port: {port}");
                if (!string.IsNullOrEmpty(comm.chipNum))
                    sb.AppendLine($"Chip Serial: {comm.chipNum}");
                if (!string.IsNullOrEmpty(comm.storageInfo))
                    sb.AppendLine($"Storage: {comm.storageInfo}");
                sb.AppendLine($"DDR4: {comm.isDdr4}, DDR5: {comm.isDdr5}");
                sb.AppendLine($"Partial Reset: {comm.isSupportPartialReset}");
                sb.AppendLine($"EDL Auth Needed: {comm.needEdlAuth}");

                SetDeviceInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                SetDeviceInfo($"Read failed: {ex.Message}");
            }
            finally
            {
                try { comm?.StopReading(); } catch { }
                try { serialPort?.Close(); } catch { }
            }
        });
    }

    private void BtnQcExploitCheck_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        SetDeviceInfo("Checking vulnerabilities...");
        RunAsync(async () =>
        {
            Comm comm = null;
            SerialPort serialPort = null;
            try
            {
                comm = new Comm();
                serialPort = new SerialPort(port, 9600);
                comm.serialPort = serialPort;
                serialPort.Open();
                comm.StartReading();

                int retries = 10;
                uint chipId = 0;
                while (retries-- > 0)
                {
                    comm.getRecDataIgnoreExcep();
                    if (comm.recData != null && comm.recData.Length >= 4)
                    {
                        uint cmd = BitConverter.ToUInt32(comm.recData, 0);
                        if (cmd == 1) { chipId = BitConverter.ToUInt32(comm.recData, 20); break; }
                    }
                    await Task.Delay(500);
                }

                string configure = string.Format(v1per_wpf.Qualcomm.Firehose.Configure, "1", 4096 * 256, "ufs", 0);
                comm.SendCommand(configure, checkAck: true);
                comm.GetResponse(waiteACK: true);

                ushort oemId = 0;
                string pkHash = comm.auth ?? "";

                var exploit = new ExploitService();
                bool hasVuln = exploit.CheckAndReportVulnerabilities(chipId, oemId, pkHash);

                var sb = new StringBuilder();
                sb.AppendLine($"Chip ID: 0x{chipId:X8}");
                sb.AppendLine($"OEM ID: 0x{oemId:X4}");
                sb.AppendLine($"PK Hash: {(pkHash.Length > 16 ? pkHash[..16] + "..." : pkHash)}");
                sb.AppendLine();
                if (hasVuln)
                {
                    sb.AppendLine("VULNERABILITY FOUND!");
                    sb.AppendLine($"Recommended: {exploit.LastCheckResult.RecommendedExploit}");
                    sb.AppendLine($"Reason: {exploit.LastCheckResult.Reason}");
                    foreach (var v in exploit.LastCheckResult.AvailableExploits)
                        sb.AppendLine($"  - {v.Name} ({v.SuccessRate}/5): {v.Description}");
                }
                else
                {
                    sb.AppendLine("No known vulnerabilities for this chip.");
                }

                SetDeviceInfo(sb.ToString());
                BtnQcExploit.IsEnabled = hasVuln && TglExploitEnable.IsChecked == true;
            }
            catch (Exception ex)
            {
                SetDeviceInfo($"Vulnerability check failed: {ex.Message}");
            }
            finally
            {
                try { comm?.StopReading(); } catch { }
                try { serialPort?.Close(); } catch { }
            }
        });
    }

    private void BtnQcExploit_Click(object sender, RoutedEventArgs e)
    {
        if (TglExploitEnable.IsChecked != true)
        {
            V1Per.Ui.Warn("Enable the exploit toggle first.");
            return;
        }
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        SetDeviceInfo("Running exploit...");
        RunAsync(async () =>
        {
            Comm comm = null;
            SerialPort serialPort = null;
            try
            {
                comm = new Comm();
                serialPort = new SerialPort(port, 9600);
                comm.serialPort = serialPort;
                serialPort.Open();
                comm.StartReading();

                int retries = 10;
                uint chipId = 0;
                while (retries-- > 0)
                {
                    comm.getRecDataIgnoreExcep();
                    if (comm.recData != null && comm.recData.Length >= 4)
                    {
                        uint cmd = BitConverter.ToUInt32(comm.recData, 0);
                        if (cmd == 1) { chipId = BitConverter.ToUInt32(comm.recData, 20); break; }
                    }
                    await Task.Delay(500);
                }

                ushort oemId = 0;
                string pkHash = comm.auth ?? "";

                var exploit = new ExploitService { EnableExploit = true };
                var result = await exploit.TryExploitIfEnabledAsync(serialPort, chipId, oemId, pkHash);

                var sb = new StringBuilder();
                sb.AppendLine($"Exploit Result: {(result.Success ? "SUCCESS" : "FAILED")}");
                sb.AppendLine($"Used: {result.UsedExploit}");
                sb.AppendLine($"Message: {result.Message}");
                if (result.ExtractedData != null)
                    sb.AppendLine($"Extracted: {result.ExtractedData.Length} bytes");

                SetDeviceInfo(sb.ToString());
            }
            catch (Exception ex)
            {
                SetDeviceInfo($"Exploit failed: {ex.Message}");
            }
            finally
            {
                try { comm?.StopReading(); } catch { }
                try { serialPort?.Close(); } catch { }
            }
        });
    }

    private void BtnQcXiaomiAuth_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        SetDeviceInfo("Xiaomi MiAuth: attempting bypass...");
        RunAsync(async () =>
        {
            Comm comm = null;
            SerialPort serialPort = null;
            try
            {
                comm = new Comm();
                serialPort = new SerialPort(port, 9600);
                comm.serialPort = serialPort;
                serialPort.Open();
                comm.StartReading();

                int retries = 10;
                while (retries-- > 0)
                {
                    comm.getRecDataIgnoreExcep();
                    if (comm.recData != null && comm.recData.Length >= 4)
                    {
                        uint cmd = BitConverter.ToUInt32(comm.recData, 0);
                        if (cmd == 1) break;
                    }
                    await Task.Delay(500);
                }

                var helloResp = new v1per_wpf.Qualcomm.sahara_hello_response();
                helloResp.Reserved = new uint[6];
                helloResp.Command = 2;
                helloResp.Length = 48;
                helloResp.Version = 2;
                helloResp.Version_min = 1;
                byte[] respBytes = CommandFormat.StructToBytes(helloResp);
                comm.WritePort(respBytes, 0, respBytes.Length);

                AppendDeviceInfo("Hello handshake done, trying signatures...");

                var auth = new XiaomiAuthStrategy();
                bool result = await auth.AuthenticateAsync(comm, "");

                if (result)
                {
                    SetDeviceInfo("Xiaomi MiAuth: BYPASS SUCCESSFUL!\nDevice is now unlocked for flashing.");
                }
                else
                {
                    string token = auth.GetAuthToken(comm);
                    if (!string.IsNullOrEmpty(token))
                    {
                        SetDeviceInfo($"Xiaomi MiAuth: Built-in signatures failed.\nAuth token: {token}\nUse this token for online authorization.");
                    }
                    else
                    {
                        SetDeviceInfo("Xiaomi MiAuth: All attempts failed.\nDevice may need signed loader or online auth.");
                    }
                }
            }
            catch (Exception ex)
            {
                SetDeviceInfo($"Xiaomi auth failed: {ex.Message}");
            }
            finally
            {
                try { comm?.StopReading(); } catch { }
                try { serialPort?.Close(); } catch { }
            }
        });
    }

    private void BtnQcOnePlusAuth_Click(object sender, RoutedEventArgs e)
    {
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0) { V1Per.Ui.Warn("No EDL port selected."); return; }
        SetDeviceInfo("OnePlus Auth: attempting Demacia/SetProjModel...");
        RunAsync(async () =>
        {
            Comm comm = null;
            SerialPort serialPort = null;
            try
            {
                comm = new Comm();
                serialPort = new SerialPort(port, 9600);
                comm.serialPort = serialPort;
                serialPort.Open();
                comm.StartReading();

                int retries = 10;
                while (retries-- > 0)
                {
                    comm.getRecDataIgnoreExcep();
                    if (comm.recData != null && comm.recData.Length >= 4)
                    {
                        uint cmd = BitConverter.ToUInt32(comm.recData, 0);
                        if (cmd == 1) break;
                    }
                    await Task.Delay(500);
                }

                var helloResp = new v1per_wpf.Qualcomm.sahara_hello_response();
                helloResp.Reserved = new uint[6];
                helloResp.Command = 2;
                helloResp.Length = 48;
                helloResp.Version = 2;
                helloResp.Version_min = 1;
                byte[] respBytes = CommandFormat.StructToBytes(helloResp);
                comm.WritePort(respBytes, 0, respBytes.Length);

                AppendDeviceInfo("Hello handshake done, starting OnePlus auth...");

                var auth = new OnePlusAuthStrategy();
                bool result = await auth.AuthenticateAsync(comm, "");

                if (result)
                    SetDeviceInfo("OnePlus Auth: SUCCESSFUL!\nDevice is now unlocked for flashing.");
                else
                    SetDeviceInfo("OnePlus Auth: All attempts failed.\nDevice may not be OnePlus or needs different approach.");
            }
            catch (Exception ex)
            {
                SetDeviceInfo($"OnePlus auth failed: {ex.Message}");
            }
            finally
            {
                try { comm?.StopReading(); } catch { }
                try { serialPort?.Close(); } catch { }
            }
        });
    }

    private void BtnQcGptBackup_Click(object sender, RoutedEventArgs e)
    {
        string fw = TxtQcFirmware.Text.Trim().Trim('"', '\'');
        if (fw.Length == 0 || !Directory.Exists(fw))
        {
            V1Per.Ui.Warn("Pick a firmware folder with rawprogram0.xml first.");
            return;
        }
        var gptSvc = new V1Per.Qualcomm.Services.GptService();
        string rawProgram = System.IO.Path.Combine(fw, "rawprogram0.xml");
        if (!System.IO.File.Exists(rawProgram))
        {
            V1Per.Ui.Warn("rawprogram0.xml not found in firmware folder.");
            return;
        }
        var result = gptSvc.BackupFromRawProgram(rawProgram, null);
        if (result.Success)
            TxtDeviceInfo.Text = $"GPT backup saved: {result.OutputPath}\n{result.PartitionCount} partitions.";
        else
            TxtDeviceInfo.Text = $"GPT backup failed: {result.Message}";
    }

    private void BtnQcGptFromXml_Click(object sender, RoutedEventArgs e) => BtnQcGptBackup_Click(sender, e);

    private void BtnQcConvertBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Firmware files (*.ofp;*.ozip;*.ops;*.zip;*.tgz)|*.ofp;*.ozip;*.ops;*.zip;*.tgz|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            TxtConvertInput.Text = dlg.FileName;
    }

    private void BtnQcConvert_Click(object sender, RoutedEventArgs e)
    {
        string input = TxtConvertInput.Text.Trim();
        if (string.IsNullOrEmpty(input) || !System.IO.File.Exists(input))
        {
            V1Per.Ui.Warn("Select a valid firmware file first.");
            return;
        }
        var conv = new V1Per.Qualcomm.Services.FirmwareConvertService();
        var result = conv.ConvertFirmware(input, null);
        TxtDeviceInfo.Text = result.Success
            ? $"Converted: {result.FilesExtracted} files\nOutput: {result.OutputDir}"
            : $"{result.Message}";
    }

    private void BtnQcProvisionParse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Provision XML (*.xml)|*.xml" };
        if (dlg.ShowDialog() == true)
        {
            var svc = new V1Per.Qualcomm.Services.ProvisionService();
            var config = svc.ParseProvisionXml(dlg.FileName);
            TxtDeviceInfo.Text = svc.GenerateAnalysisReport(config);
        }
    }

    private void BtnQcProvisionDefault_Click(object sender, RoutedEventArgs e)
    {
        var config = V1Per.Qualcomm.Services.ProvisionService.CreateDefaultUfsConfig(256);
        var svc = new V1Per.Qualcomm.Services.ProvisionService();
        TxtDeviceInfo.Text = svc.GenerateAnalysisReport(config);
    }

    private void RunQcOp(string op)
    {
        string fw = TxtQcFirmware.Text.Trim().Trim('"', '\'');
        if (fw.Length == 0 || !Directory.Exists(fw))
        {
            V1Per.Ui.Warn("Pick a valid firmware folder first.");
            return;
        }
        string port = ComboEdlPort.SelectedItem as string ?? "";
        if (port.Length == 0)
        {
            V1Per.Ui.Warn("No EDL port selected. Refresh the port list.");
            return;
        }
        RunAsync(async () =>
        {
            if (op == "flash")
                await QcFlash.Run(new[] { "flash", port, fw });
            else
                await QcFlash.Run(new[] { "dump", port, fw });
        });
    }

    // -- Tools tab --------------------------------------------------

    private void BtnForceFastboot_Click(object sender, RoutedEventArgs e) => RunCommand("force");
    private void BtnDevices_Click(object sender, RoutedEventArgs e) => RunCommand("devices");
    private void BtnRoot_Click(object sender, RoutedEventArgs e) => RunCommand("root");
    private void BtnScatter_Click(object sender, RoutedEventArgs e) => RunCommand("scatter");
    private void BtnAnyKernel_Click(object sender, RoutedEventArgs e) => RunCommand("anykernel");
    private void BtnDrivers_Click(object sender, RoutedEventArgs e) => RunCommand("drivers");
    private void BtnScrcpy_Click(object sender, RoutedEventArgs e) => RunCommand("scrcpy");
    private void BtnDemo_Click(object sender, RoutedEventArgs e) => RunCommand("demo");

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        V1Per.ProcessRunner.KillAll();
        V1Per.Ui.Warn("STOP requested. Running processes killed.");
    }

    // -- MediaTek Tools tab -------------------------------------------

    private void BtnMtkConnect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_mtkService.DaFilePath) && string.IsNullOrEmpty(TxtMtkDaFile.Text))
        {
            V1Per.Ui.Warn("Browse and select a DA file first.");
            return;
        }
        BtnMtkConnect.IsEnabled = false;
        RunAsync(async () =>
        {
            bool connected = await _mtkService.ConnectAsync();
            if (connected && !string.IsNullOrEmpty(_mtkService.DaFilePath))
            {
                V1Per.Ui.Muted("Device connected, loading DA...");
                await _mtkService.LoadDaAsync();
            }
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!connected)
                    BtnMtkConnect.IsEnabled = true;
            }));
        });
    }

    private void BtnMtkDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _mtkService.Disconnect();
        V1Per.Ui.Muted("MediaTek device disconnected.");
    }

    private void BtnMtkBrowseDa_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select MTK DA file",
            Filter = "DA files (*.bin;*.da)|*.bin;*.da|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            TxtMtkDaFile.Text = dlg.FileName;
            _mtkService.SetDaFilePath(dlg.FileName);
        }
    }

    private void BtnMtkReadInfo_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () =>
        {
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                var info = _mtkService.GetSecurityInfo();
                var chip = _mtkService.ChipInfo;
                var exp = _mtkService.GetExploitInfo();

                var sb = new StringBuilder();
                if (chip != null)
                {
                    sb.AppendLine($"Chip: {chip.GetChipName()} (0x{chip.HwCode:X4})");
                    sb.AppendLine($"HW Ver: 0x{chip.HwVer:X}");
                    sb.AppendLine($"DA Payload: 0x{chip.DaPayloadAddr:X}");
                }
                if (info != null)
                {
                    sb.AppendLine($"SBC: {info.SbcEnabled} | SLA: {info.SlaEnabled} | DAA: {info.DaaEnabled}");
                    if (!string.IsNullOrEmpty(info.MeId))
                        sb.AppendLine($"ME ID: {info.MeId[..Math.Min(16, info.MeId.Length)]}...");
                }
                if (exp != null)
                {
                    sb.AppendLine($"Exploit: {exp.ExploitType}");
                    sb.AppendLine($"Carbonara: {exp.IsCarbonaraSupported}");
                }
                TxtMtkDeviceInfo.Text = sb.ToString();
            }));
        });
    }

    private void BtnMtkReboot_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () => await _mtkService.RebootAsync());
    }

    private void BtnMtkFlash_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () =>
        {
            string partition = await Dialogs.PromptAsync("Partition name (e.g. boot):");
            if (partition.Length == 0) return;
            string image = await Dialogs.PromptAsync("Image file path:");
            if (image.Length == 0) return;
            await _mtkService.WritePartitionAsync(partition, image);
        });
    }

    private void BtnMtkReadPart_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () =>
        {
            string partition = await Dialogs.PromptAsync("Partition name to read:");
            if (partition.Length == 0) return;
            string sizeStr = await Dialogs.PromptAsync("Size in bytes (e.g. 67108864):");
            if (!ulong.TryParse(sizeStr, out ulong size)) size = 64 * 1024 * 1024;
            var dlg = new SaveFileDialog { Title = "Save partition dump", FileName = $"{partition}.bin" };
            if (dlg.ShowDialog(this) == true)
            {
                await _mtkService.ReadPartitionAsync(partition, dlg.FileName, size);
            }
        });
    }

    private void BtnMtkErase_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () =>
        {
            string partition = await Dialogs.PromptAsync("Partition name to erase:");
            if (partition.Length == 0) return;
            await _mtkService.ErasePartitionAsync(partition);
        });
    }

    private void BtnMtkLoadScatter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select MTK scatter file",
            Filter = "Scatter files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        var parts = Scatter.Parse(dlg.FileName);
        if (parts.Count == 0) return;
        _mtkPartitions.Clear();
        foreach (var part in parts)
        {
            _mtkPartitions.Add(new MtkPartitionRow
            {
                IsChecked = part.IsDownloadable && !part.Unsafe,
                Name = part.Name,
                FileName = part.FileName,
                SizeHuman = part.SizeHuman(),
                Path = part.ResolvedPath ?? string.Empty
            });
        }
        V1Per.Ui.Ok($"Loaded {parts.Count} partitions from scatter.");
    }

    private void BtnMtkExploitCheck_Click(object sender, RoutedEventArgs e)
    {
        RunAsync(async () =>
        {
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                var exp = _mtkService.GetExploitInfo();
                var sb = new StringBuilder();
                sb.AppendLine($"Chip: {exp.ChipName} (0x{exp.HwCode:X4})");
                sb.AppendLine($"Exploit Type: {exp.ExploitType}");
                sb.AppendLine($"Carbonara: {exp.IsCarbonaraSupported}");
                sb.AppendLine($"AllinoneSignature: {exp.IsAllinoneSignatureSupported}");
                TxtMtkDeviceInfo.Text = sb.ToString();
            }));
        });
    }

    private void BtnMtkBromExploit_Click(object sender, RoutedEventArgs e)
    {
        if (TglMtkExploitEnable.IsChecked != true)
        {
            V1Per.Ui.Warn("Enable the exploit toggle first.");
            return;
        }
        RunAsync(async () => await _mtkService.RunBromExploitAsync());
    }
}

/// <summary>Row model for the partition flash grid.</summary>
public sealed class PartitionRow
{
    public bool IsChecked { get; set; }
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Size { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Row model for the MTK partition grid.</summary>
public sealed class MtkPartitionRow
{
    public bool IsChecked { get; set; }
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string SizeHuman { get; set; } = "";
    public string Path { get; set; } = "";
}