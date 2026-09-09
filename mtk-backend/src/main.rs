use nusb::descriptors::TransferType;
use nusb::io::{EndpointRead, EndpointWrite};
use nusb::transfer::{Bulk, ControlOut, ControlType, Direction, In, Out, Recipient};
use nusb::{Device, DeviceInfo, Interface, MaybeFuture};
use serde::Serialize;
use serialport::{ClearBuffer, SerialPort, SerialPortInfo, SerialPortType};
use std::io::{self, Read, Write};
use std::time::{Duration, Instant};

#[cfg(windows)]
type NativeSerial = serialport::COMPort;
#[cfg(not(windows))]
type NativeSerial = serialport::TTYPort;

trait MtkPort {
    fn open(&mut self) -> Result<(), String>;
    fn close(&mut self);
    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()>;
    fn write_all(&mut self, buf: &[u8]) -> io::Result<()>;
    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()>;
    fn conn_type(&self) -> ConnType;
    fn port_name(&self) -> String;
}

const MTK_VID: u16 = 0x0E8D;
const BULK_IN_SZ: usize = 0x80000;
const BULK_OUT_SZ: usize = 0x80000;
const MIN_TIMEOUT: Duration = Duration::from_millis(1000);
const MAX_TIMEOUT: Duration = Duration::from_millis(10000);

const HANDSHAKE_SEQ: [u8; 4] = [0xA0, 0x0A, 0x50, 0x05];

#[derive(Debug, PartialEq, Eq, Copy, Clone, Serialize)]
#[serde(rename_all = "lowercase")]
enum ConnType {
    Brom,
    Preloader,
    Da,
}

impl ConnType {
    fn as_str(&self) -> &'static str {
        match self {
            ConnType::Brom => "brom",
            ConnType::Preloader => "preloader",
            ConnType::Da => "da",
        }
    }
}

const KNOWN_PORTS: &[(u16, u16, ConnType)] = &[
    (0x0E8D, 0x0003, ConnType::Brom),
    (0x0E8D, 0x6000, ConnType::Preloader),
    (0x0E8D, 0x2000, ConnType::Preloader),
    (0x0E8D, 0x2001, ConnType::Da),
    (0x0E8D, 0x20FF, ConnType::Preloader),
    (0x0E8D, 0x3000, ConnType::Preloader),
];

#[repr(u8)]
#[derive(Debug, Clone, Copy)]
enum Command {
    JumpDa = 0xD5,
    SendDa = 0xD7,
    GetTargetConfig = 0xD8,
    GetMeId = 0xE1,
    GetSocId = 0xE7,
    GetPlCap = 0xFB,
    GetHwSwVer = 0xFC,
    GetHwCode = 0xFD,
    GetPlVer = 0xFE,
    GetBrVer = 0xFF,
}

#[derive(Serialize)]
struct LocalDeviceInfo {
    vid: u16,
    pid: u16,
    mode: String,
    product: String,
    manufacturer: String,
}

#[derive(Serialize)]
struct HandshakeResult {
    success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    hw_code: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    hw_sub_code: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    hw_ver: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    sw_ver: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    bl_version: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    target_config: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    soc_id: Option<Vec<u8>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    me_id: Option<Vec<u8>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    connection_type: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

#[derive(Serialize)]
struct DaUploadResult {
    success: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    address: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    size: Option<usize>,
    #[serde(skip_serializing_if = "Option::is_none")]
    sig_len: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

#[derive(Serialize)]
struct Response {
    status: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    devices: Option<Vec<LocalDeviceInfo>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    handshake: Option<HandshakeResult>,
    #[serde(skip_serializing_if = "Option::is_none")]
    da_upload: Option<DaUploadResult>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}

impl Response {
    fn error(msg: String) -> Self {
        Response {
            status: "error".into(),
            devices: None,
            handshake: None,
            da_upload: None,
            error: Some(msg),
        }
    }
}

struct UsbMtkPort {
    info: DeviceInfo,
    interface: Option<Interface>,
    ctrl_interface: Option<Interface>,
    reader: Option<EndpointRead<Bulk>>,
    writer: Option<EndpointWrite<Bulk>>,
    ep_out: u8,
    ep_in: u8,
    conn_type: ConnType,
    is_open: bool,
    timeout: Duration,
}

impl UsbMtkPort {
    fn new(info: DeviceInfo, conn_type: ConnType) -> Self {
        Self {
            info,
            interface: None,
            ctrl_interface: None,
            reader: None,
            writer: None,
            ep_out: 0,
            ep_in: 0,
            conn_type,
            is_open: false,
            timeout: MIN_TIMEOUT,
        }
    }

    fn find_mtk_device() -> Result<(DeviceInfo, ConnType), String> {
        let devices = nusb::list_devices().wait().map_err(|e| format!("Failed to list USB devices: {e:?}"))?;
        for device in devices {
            let vid = device.vendor_id();
            let pid = device.product_id();
            if let Some((_, _, ct)) = KNOWN_PORTS.iter().find(|(v, p, _)| *v == vid && *p == pid) {
                return Ok((device, *ct));
            }
        }
        Err("No MTK device found".into())
    }

    fn find_cdc_interface_numbers(device: &Device) -> Result<(u8, u8), String> {
        let settings: Vec<(u8, u8)> = device
            .configurations()
            .flat_map(|c| c.interfaces())
            .flat_map(|i| i.alt_settings().map(|a| (a.class(), a.interface_number())).collect::<Vec<_>>())
            .collect();
        let ctrl_num = settings.iter().find(|(class, _)| *class == 2).map(|(_, n)| *n);
        let bulk_num = settings.iter().find(|(class, _)| *class == 10).map(|(_, n)| *n);
        match (ctrl_num, bulk_num) {
            (Some(c), Some(b)) => Ok((c, b)),
            _ => Err("CDC interfaces not found".into()),
        }
    }

    fn select_endpoints(&mut self, iface: &Interface) -> Result<(), String> {
        for alt in iface.descriptors() {
            let mut in_ep = None;
            let mut out_ep = None;
            for ep in alt.endpoints() {
                if !matches!(ep.transfer_type(), TransferType::Bulk) {
                    continue;
                }
                match ep.direction() {
                    Direction::In => in_ep = Some(ep.address()),
                    Direction::Out => out_ep = Some(ep.address()),
                }
            }
            if let (Some(i), Some(o)) = (in_ep, out_ep) {
                self.ep_in = i;
                self.ep_out = o;
                return Ok(());
            }
        }
        Err("No bulk endpoints found".into())
    }

    fn setup_cdc(&self) -> Result<(), String> {
        const SET_LINE_CODING: u8 = 0x20;
        const SET_CONTROL_LINE_STATE: u8 = 0x22;
        const LINE_CODING: [u8; 7] = [0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x08];
        const CONTROL_LINE_STATE: u16 = 0x03;

        let iface = self.ctrl_interface.as_ref().ok_or("port not open")?;
        iface
            .control_out(
                ControlOut {
                    control_type: ControlType::Class,
                    recipient: Recipient::Interface,
                    request: SET_LINE_CODING,
                    value: 0,
                    index: 0,
                    data: &LINE_CODING,
                },
                MIN_TIMEOUT,
            )
            .wait()
            .map_err(|_| "SET_LINE_CODING failed".to_string())?;
        iface
            .control_out(
                ControlOut {
                    control_type: ControlType::Class,
                    recipient: Recipient::Interface,
                    request: SET_CONTROL_LINE_STATE,
                    value: CONTROL_LINE_STATE,
                    index: 0,
                    data: &[],
                },
                MIN_TIMEOUT,
            )
            .wait()
            .map_err(|_| "SET_CONTROL_LINE_STATE failed".to_string())?;
        Ok(())
    }

    fn open(&mut self) -> Result<(), String> {
        if self.is_open {
            return Ok(());
        }
        let device = self.info.open().wait().map_err(|e| format!("Failed to open device: {e:?}"))?;
        let (ctrl_num, bulk_num) = Self::find_cdc_interface_numbers(&device)?;

        self.ctrl_interface = Some(match device.detach_and_claim_interface(ctrl_num).wait() {
            Ok(i) => i,
            Err(e) => {
                if matches!(e.kind(), nusb::ErrorKind::Unsupported) {
                    return Err(
                        "Control interface claim failed: incompatible driver installed. \
                         Install the WinUSB driver for this device with Zadig (zadig.akeo.ie)."
                            .into(),
                    );
                }
                return Err(format!("Failed to claim control interface: {e:?}"));
            }
        });
        let bulk_iface = device
            .detach_and_claim_interface(bulk_num)
            .wait()
            .map_err(|e| format!("Failed to claim bulk interface: {e:?}"))?;

        self.select_endpoints(&bulk_iface)?;
        let tr = if cfg!(windows) { 1 } else { 8 };

        self.reader = Some(
            bulk_iface
                .endpoint::<Bulk, In>(self.ep_in)
                .map_err(|e| format!("Failed to get IN endpoint: {e:?}"))?
                .reader(BULK_IN_SZ)
                .with_num_transfers(tr)
                .with_read_timeout(MIN_TIMEOUT),
        );
        self.writer = Some(
            bulk_iface
                .endpoint::<Bulk, Out>(self.ep_out)
                .map_err(|e| format!("Failed to get OUT endpoint: {e:?}"))?
                .writer(BULK_OUT_SZ)
                .with_num_transfers(tr)
                .with_write_timeout(MIN_TIMEOUT),
        );
        self.interface = Some(bulk_iface);

        if self.conn_type != ConnType::Brom {
            let _ = self.setup_cdc();
        }
        self.is_open = true;
        Ok(())
    }

    fn close(&mut self) {
        self.reader = None;
        self.writer = None;
        self.interface = None;
        self.ctrl_interface = None;
        self.is_open = false;
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        let reader = self
            .reader
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        reader.read_exact(buf)
    }

    fn write_all(&mut self, buf: &[u8]) -> io::Result<()> {
        let writer = self
            .writer
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        writer.write_all(buf)?;
        writer.flush()
    }

    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        let reader = self
            .reader
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        let writer = self
            .writer
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        reader.set_read_timeout(timeout);
        writer.set_write_timeout(timeout);
        self.timeout = timeout;
        Ok(())
    }

    fn conn_type(&self) -> ConnType {
        self.conn_type
    }

    fn port_name(&self) -> String {
        format!("USB {:04X}:{:04X}", self.info.vendor_id(), self.info.product_id())
    }
}

impl MtkPort for UsbMtkPort {
    fn open(&mut self) -> Result<(), String> {
        UsbMtkPort::open(self)
    }

    fn close(&mut self) {
        UsbMtkPort::close(self);
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        UsbMtkPort::read_exact(self, buf)
    }

    fn write_all(&mut self, buf: &[u8]) -> io::Result<()> {
        UsbMtkPort::write_all(self, buf)
    }

    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        UsbMtkPort::set_timeout(self, timeout)
    }

    fn conn_type(&self) -> ConnType {
        UsbMtkPort::conn_type(self)
    }

    fn port_name(&self) -> String {
        UsbMtkPort::port_name(self)
    }
}

struct SerialMtkPort {
    port_info: SerialPortInfo,
    port: Option<NativeSerial>,
    baudrate: u32,
    conn_type: ConnType,
    is_open: bool,
}

impl SerialMtkPort {
    fn new(port_info: SerialPortInfo, conn_type: ConnType) -> Self {
        let baudrate = match conn_type {
            ConnType::Brom => 115_200,
            ConnType::Preloader | ConnType::Da => 921_600,
        };
        Self {
            port_info,
            port: None,
            baudrate,
            conn_type,
            is_open: false,
        }
    }

    fn find_mtk_serial() -> Option<(SerialPortInfo, ConnType)> {
        let ports = serialport::available_ports().unwrap_or_default();
        for port_info in ports {
            if let SerialPortType::UsbPort(usb) = &port_info.port_type {
                if let Some((_, _, ct)) =
                    KNOWN_PORTS.iter().find(|(v, p, _)| *v == usb.vid && *p == usb.pid)
                {
                    return Some((port_info, *ct));
                }
            }
        }
        None
    }
}

impl MtkPort for SerialMtkPort {
    fn open(&mut self) -> Result<(), String> {
        if self.is_open {
            return Ok(());
        }
        let port = serialport::new(&self.port_info.port_name, self.baudrate)
            .timeout(MIN_TIMEOUT)
            .open_native()
            .map_err(|e| format!("Failed to open serial port {}: {e}", self.port_info.port_name))?;
        self.port = Some(port);
        self.is_open = true;
        Ok(())
    }

    fn close(&mut self) {
        if let Some(mut port) = self.port.take() {
            let _ = port.clear(ClearBuffer::All);
        }
        self.is_open = false;
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        let port = self
            .port
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        port.read_exact(buf)
    }

    fn write_all(&mut self, buf: &[u8]) -> io::Result<()> {
        let port = self
            .port
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        port.write_all(buf)
    }

    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        let port = self
            .port
            .as_mut()
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotConnected, "port not open"))?;
        port.set_timeout(timeout)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e.to_string()))?;
        Ok(())
    }

    fn conn_type(&self) -> ConnType {
        self.conn_type
    }

    fn port_name(&self) -> String {
        self.port_info.port_name.clone()
    }
}

enum MtkDevice {
    Usb(UsbMtkPort),
    Serial(SerialMtkPort),
}

impl MtkDevice {
    fn open(&mut self) -> Result<(), String> {
        match self {
            MtkDevice::Usb(p) => p.open(),
            MtkDevice::Serial(p) => p.open(),
        }
    }

    fn close(&mut self) {
        match self {
            MtkDevice::Usb(p) => p.close(),
            MtkDevice::Serial(p) => p.close(),
        }
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        match self {
            MtkDevice::Usb(p) => p.read_exact(buf),
            MtkDevice::Serial(p) => p.read_exact(buf),
        }
    }

    fn write_all(&mut self, buf: &[u8]) -> io::Result<()> {
        match self {
            MtkDevice::Usb(p) => p.write_all(buf),
            MtkDevice::Serial(p) => p.write_all(buf),
        }
    }

    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        match self {
            MtkDevice::Usb(p) => p.set_timeout(timeout),
            MtkDevice::Serial(p) => p.set_timeout(timeout),
        }
    }

    fn conn_type(&self) -> ConnType {
        match self {
            MtkDevice::Usb(p) => p.conn_type(),
            MtkDevice::Serial(p) => p.conn_type(),
        }
    }

    fn port_name(&self) -> String {
        match self {
            MtkDevice::Usb(p) => p.port_name(),
            MtkDevice::Serial(p) => p.port_name(),
        }
    }
}

impl MtkPort for MtkDevice {
    fn open(&mut self) -> Result<(), String> {
        MtkDevice::open(self)
    }

    fn close(&mut self) {
        MtkDevice::close(self);
    }

    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> {
        MtkDevice::read_exact(self, buf)
    }

    fn write_all(&mut self, buf: &[u8]) -> io::Result<()> {
        MtkDevice::write_all(self, buf)
    }

    fn set_timeout(&mut self, timeout: Duration) -> io::Result<()> {
        MtkDevice::set_timeout(self, timeout)
    }

    fn conn_type(&self) -> ConnType {
        MtkDevice::conn_type(self)
    }

    fn port_name(&self) -> String {
        MtkDevice::port_name(self)
    }
}

fn find_and_open() -> Result<MtkDevice, String> {
    if let Ok((info, ct)) = UsbMtkPort::find_mtk_device() {
        let mut port = UsbMtkPort::new(info, ct);
        match port.open() {
            Ok(()) => return Ok(MtkDevice::Usb(port)),
            Err(_) => {}
        }
    }

    if let Some((info, ct)) = SerialMtkPort::find_mtk_serial() {
        let mut port = SerialMtkPort::new(info, ct);
        match port.open() {
            Ok(()) => return Ok(MtkDevice::Serial(port)),
            Err(e) => return Err(e),
        }
    }

    Err("No MTK device found (USB or VCOM)".into())
}

struct PlProtocol<'a, P: MtkPort> {
    port: &'a mut P,
}

impl<'a, P: MtkPort> PlProtocol<'a, P> {
    fn new(port: &'a mut P) -> Self {
        Self { port }
    }

    fn read_u16_be(&mut self) -> io::Result<u16> {
        let mut buf = [0u8; 2];
        self.port.read_exact(&mut buf)?;
        Ok(u16::from_be_bytes(buf))
    }

    fn read_u32_be(&mut self) -> io::Result<u32> {
        let mut buf = [0u8; 4];
        self.port.read_exact(&mut buf)?;
        Ok(u32::from_be_bytes(buf))
    }

    fn echo(&mut self, data: &[u8], size: usize) -> io::Result<()> {
        self.port.write_all(data)?;
        let mut buf = vec![0u8; size];
        self.port.read_exact(&mut buf)?;
        if buf != data {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("echo mismatch: sent {data:02X?}, got {buf:02X?}"),
            ));
        }
        Ok(())
    }

    fn status_ok(&mut self) -> io::Result<()> {
        let status = self.read_u16_be()?;
        if status != 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("status not ok: 0x{status:04X}"),
            ));
        }
        Ok(())
    }

    fn handshake(&mut self) -> io::Result<()> {
        let retries = 5;
        let mut last_err = None;

        // Preloader spams "READY"; send the first SEQ byte so it detects us.
        if self.port.conn_type() != ConnType::Brom {
            let _ = self.port.write_all(&[HANDSHAKE_SEQ[0]]);
        }
        self.port.set_timeout(MAX_TIMEOUT)?;

        for _ in 0..retries {
            match self.handshake_seq() {
                Ok(()) => {
                    self.port.set_timeout(MIN_TIMEOUT)?;
                    return Ok(());
                }
                Err(e) => {
                    last_err = Some(e);
                    std::thread::sleep(Duration::from_millis(10));
                    self.port.set_timeout(Duration::from_millis(50))?;
                    while self.port.read_exact(&mut [0u8; 1]).is_ok() {}
                    self.port.set_timeout(MAX_TIMEOUT)?;
                }
            }
        }

        self.port.set_timeout(MIN_TIMEOUT)?;
        Err(last_err.unwrap_or_else(|| io::Error::new(io::ErrorKind::TimedOut, "handshake failed")))
    }

    fn handshake_seq(&mut self) -> io::Result<()> {
        for &byte in &HANDSHAKE_SEQ {
            self.port.write_all(&[byte])?;
            let mut resp = [0u8; 1];
            self.port.read_exact(&mut resp)?;
            let expected = byte ^ 0xFF;
            if resp[0] == expected {
                continue;
            } else if resp[0] == HANDSHAKE_SEQ[0] {
                // Already handshaken; preloader just echoes the first byte.
                return Ok(());
            } else {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    format!("handshake mismatch: expected 0x{expected:02X}, got 0x{:02X}", resp[0]),
                ));
            }
        }
        Ok(())
    }

    fn get_hw_code(&mut self) -> io::Result<u16> {
        self.echo(&[Command::GetHwCode as u8], 1)?;
        let code = self.read_u16_be()?;
        self.status_ok()?;
        Ok(code)
    }

    fn get_hw_sw_ver(&mut self) -> io::Result<(u16, u16, u16)> {
        self.echo(&[Command::GetHwSwVer as u8], 1)?;
        let sub = self.read_u16_be()?;
        let hw = self.read_u16_be()?;
        let sw = self.read_u16_be()?;
        self.status_ok()?;
        Ok((sub, hw, sw))
    }

    fn get_target_config(&mut self) -> io::Result<u32> {
        self.echo(&[Command::GetTargetConfig as u8], 1)?;
        let cfg = self.read_u32_be()?;
        self.status_ok()?;
        Ok(cfg)
    }

    fn get_pl_capabilities(&mut self) -> io::Result<u32> {
        self.echo(&[Command::GetPlCap as u8], 1)?;
        let cap = self.read_u32_be()?;
        let _reserved = self.read_u32_be()?;
        Ok(cap)
    }

    fn get_pl_version(&mut self) -> io::Result<u16> {
        self.echo(&[Command::GetPlVer as u8], 1)?;
        let ver = self.read_u16_be()?;
        self.status_ok()?;
        Ok(ver)
    }

    fn get_br_version(&mut self) -> io::Result<u16> {
        self.echo(&[Command::GetBrVer as u8], 1)?;
        let ver = self.read_u16_be()?;
        self.status_ok()?;
        Ok(ver)
    }

    fn get_soc_id(&mut self) -> io::Result<Option<Vec<u8>>> {
        self.echo(&[Command::GetSocId as u8], 1)?;
        let len = self.read_u32_be()? as usize;
        if len == 0 || len > 32 {
            self.status_ok()?;
            return Ok(None);
        }
        let mut id = vec![0u8; len];
        self.port.read_exact(&mut id)?;
        self.status_ok()?;
        Ok(Some(id))
    }

    fn get_me_id(&mut self) -> io::Result<Option<Vec<u8>>> {
        self.echo(&[Command::GetMeId as u8], 1)?;
        let len = self.read_u32_be()? as usize;
        if len == 0 || len > 16 {
            self.status_ok()?;
            return Ok(None);
        }
        let mut id = vec![0u8; len];
        self.port.read_exact(&mut id)?;
        self.status_ok()?;
        Ok(Some(id))
    }

    fn send_da(&mut self, address: u32, data: &[u8], sig_len: u32) -> io::Result<()> {
        self.echo(&[Command::SendDa as u8], 1)?;
        self.echo(&address.to_be_bytes(), 4)?;
        self.echo(&(data.len() as u32).to_be_bytes(), 4)?;
        self.echo(&sig_len.to_be_bytes(), 4)?;
        self.status_ok()?;

        const CHUNK: usize = 0x400;
        for chunk in data.chunks(CHUNK) {
            self.port.write_all(chunk)?;
        }

        // Device replies with the XOR checksum of the DA data, then a status word.
        let _checksum = self.read_u16_be()?;
        self.status_ok()?;
        Ok(())
    }

    fn jump_da(&mut self, address: u32) -> io::Result<()> {
        self.echo(&[Command::JumpDa as u8], 1)?;
        self.echo(&address.to_be_bytes(), 4)?;
        self.status_ok()?;
        Ok(())
    }
}

const XF_MAGIC: u32 = 0xFEEEEEEF;
const XF_SYNC_SIGNAL: u32 = 0x434E5953;

#[derive(Debug, Clone, Copy)]
#[repr(u32)]
enum XfCmd {
    Download = 0x010001,
    Upload = 0x010002,
    Format = 0x010003,
    ReadData = 0x010005,
    Shutdown = 0x010007,
    BootTo = 0x010008,
    DeviceCtrl = 0x010009,
    InitExtRam = 0x01000A,
    SetupEnvironment = 0x010100,
    SetupHwInitParams = 0x010101,
    SetChecksumLevel = 0x020003,
    GetConnectionAgent = 0x04000A,
    GetPacketLength = 0x040007,
    GetPartitionTblCata = 0x040009,
    GetChipId = 0x04000D,
    GetDaVersion = 0x040005,
    GetEmmcInfo = 0x040001,
    GetUfsInfo = 0x040004,
}

struct XFlash<'a, P: MtkPort> {
    port: &'a mut P,
    write_packet_len: usize,
}

impl<'a, P: MtkPort> XFlash<'a, P> {
    fn new(port: &'a mut P) -> Self {
        Self { port, write_packet_len: 0x8000 }
    }

    fn write_packet(&mut self, data: &[u8]) -> io::Result<()> {
        let mut hdr = [0u8; 12];
        hdr[0..4].copy_from_slice(&XF_MAGIC.to_le_bytes());
        hdr[4..8].copy_from_slice(&1u32.to_le_bytes());
        hdr[8..12].copy_from_slice(&(data.len() as u32).to_le_bytes());
        self.port.write_all(&hdr)?;
        let max = self.write_packet_len;
        let mut pos = 0;
        while pos < data.len() {
            let end = (pos + max).min(data.len());
            self.port.write_all(&data[pos..end])?;
            pos = end;
        }
        Ok(())
    }

    fn read_packet(&mut self) -> io::Result<Vec<u8>> {
        let mut hdr = [0u8; 12];
        self.port.read_exact(&mut hdr)?;
        let magic = u32::from_le_bytes(hdr[0..4].try_into().unwrap());
        let dtype = u32::from_le_bytes(hdr[4..8].try_into().unwrap());
        let len = u32::from_le_bytes(hdr[8..12].try_into().unwrap());
        if magic != XF_MAGIC {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("bad XFlash magic 0x{magic:08X}"),
            ));
        }
        if dtype == 2 {
            let mut payload = vec![0u8; len as usize];
            self.port.read_exact(&mut payload)?;
            return self.read_packet();
        }
        let mut data = vec![0u8; len as usize];
        self.port.read_exact(&mut data)?;
        Ok(data)
    }

    fn read_status(&mut self) -> io::Result<u32> {
        let data = self.read_packet()?;
        if data.is_empty() {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "empty status"));
        }
        Ok(u32::from_le_bytes(data[..4].try_into().unwrap()))
    }

    fn status_ok(&mut self) -> io::Result<()> {
        let status = self.read_status()?;
        if status != 0 {
            return Err(io::Error::new(
                io::ErrorKind::Other,
                format!("XFlash status 0x{status:08X}"),
            ));
        }
        Ok(())
    }

    fn send_data(&mut self, chunks: &[&[u8]]) -> io::Result<()> {
        for chunk in chunks {
            self.write_packet(chunk)?;
        }
        self.status_ok()?;
        Ok(())
    }

    fn send_cmd(&mut self, cmd: XfCmd) -> io::Result<()> {
        let b = (cmd as u32).to_le_bytes();
        self.send_data(&[&b])
    }

    fn devctrl(&mut self, cmd: XfCmd, params: Option<&[&[u8]]>) -> io::Result<Vec<u8>> {
        self.send_cmd(XfCmd::DeviceCtrl)?;
        self.send_cmd(cmd)?;
        if let Some(p) = params {
            self.send_data(p)?;
            return Ok(vec![]);
        }
        let read = self.read_packet()?;
        self.status_ok()?;
        Ok(read)
    }

    fn get_packet_length(&mut self) -> io::Result<()> {
        let data = self.devctrl(XfCmd::GetPacketLength, None)?;
        if data.len() >= 8 {
            let w = u32::from_le_bytes(data[0..4].try_into().unwrap()) as usize;
            if w > 0 && w < 0x8000 {
                self.write_packet_len = w;
            }
        }
        Ok(())
    }

    fn boot_to(&mut self, addr: u32, data: &[u8]) -> io::Result<()> {
        self.send_cmd(XfCmd::BootTo)?;
        let mut param = [0u8; 16];
        param[0..8].copy_from_slice(&(addr as u64).to_le_bytes());
        param[8..16].copy_from_slice(&(data.len() as u64).to_le_bytes());
        self.send_data(&[&param, data])?;
        let status = self.read_status()?;
        if status != 0 && status != XF_SYNC_SIGNAL {
            return Err(io::Error::new(
                io::ErrorKind::Other,
                format!("BootTo status 0x{status:08X}"),
            ));
        }
        Ok(())
    }

    /// DA1 sync after jump: sync byte, SyncSignal, env setup, checksum level.
    fn da1_sync(&mut self) -> io::Result<()> {
        let mut sync = [0u8; 1];
        self.port.read_exact(&mut sync)?;
        if sync[0] != 0xC0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                format!("expected sync byte 0xC0, got 0x{:02X}", sync[0]),
            ));
        }

        self.write_packet(&XF_SYNC_SIGNAL.to_le_bytes())?;

        let mut env = [0u8; 20];
        env[0..4].copy_from_slice(&1u32.to_le_bytes()); // da_log_level: Info
        env[4..8].copy_from_slice(&1u32.to_le_bytes()); // log_channel: UART
        env[8..12].copy_from_slice(&0u32.to_le_bytes()); // system_os: Windows
        self.send_data(&[&(XfCmd::SetupEnvironment as u32).to_le_bytes(), &env])?;

        self.send_data(&[&(XfCmd::SetupHwInitParams as u32).to_le_bytes(), &[0u8; 4]])?;

        let status = self.read_status()?;
        if status != XF_SYNC_SIGNAL && status != 0 {
            return Err(io::Error::new(
                io::ErrorKind::Other,
                format!("DA1 sync signal 0x{status:08X}"),
            ));
        }

        let agent = self.devctrl(XfCmd::GetConnectionAgent, None)?;
        let _agent_str = String::from_utf8_lossy(&agent).trim_end_matches('\0').to_string();

        self.devctrl(XfCmd::SetChecksumLevel, Some(&[&0u32.to_le_bytes()]))?;
        Ok(())
    }

    fn detect_storage(&mut self) -> io::Result<u32> {
        match self.devctrl(XfCmd::GetEmmcInfo, None) {
            Ok(resp) => {
                eprintln!("[dbg] GetEmmcInfo -> {} bytes", resp.len());
                if resp.len() >= 4 && u32::from_le_bytes(resp[0..4].try_into().unwrap()) == 0x1 {
                    return Ok(0x1);
                }
            }
            Err(e) => eprintln!("[dbg] GetEmmcInfo err: {e}"),
        }
        match self.devctrl(XfCmd::GetUfsInfo, None) {
            Ok(resp) => {
                eprintln!("[dbg] GetUfsInfo -> {} bytes", resp.len());
                if resp.len() >= 4 && u32::from_le_bytes(resp[0..4].try_into().unwrap()) == 0x30 {
                    return Ok(0x30);
                }
            }
            Err(e) => eprintln!("[dbg] GetUfsInfo err: {e}"),
        }
        Err(io::Error::new(io::ErrorKind::Other, "unknown storage type"))
    }

    fn read_flash(&mut self, addr: u64, size: usize, storage_type: u32) -> io::Result<Vec<u8>> {
        let mut params = [0u8; 48];
        params[0..4].copy_from_slice(&storage_type.to_le_bytes());
        params[4..8].copy_from_slice(&0u32.to_le_bytes()); // partition_type: user
        params[8..16].copy_from_slice(&addr.to_le_bytes());
        params[16..24].copy_from_slice(&(size as u64).to_le_bytes());
        self.send_cmd(XfCmd::ReadData)?;
        self.send_data(&[&params])?;
        self.status_ok()?;
        let mut out = Vec::new();
        while out.len() < size {
            let chunk = self.read_packet()?;
            if chunk.is_empty() {
                break;
            }
            out.extend_from_slice(&chunk);
            self.send_data(&[&0u32.to_le_bytes()])?;
        }
        Ok(out)
    }

    fn get_chip_id(&mut self) -> io::Result<Vec<u8>> {
        self.devctrl(XfCmd::GetChipId, None)
    }
}

fn select_da2(data: &[u8], dacode: u16, hw_sub_code: u16) -> Result<(u32, Vec<u8>), String> {
    let entries = parse_da(data)?;
    let entry = entries
        .iter()
        .find(|e| e.hw_code == dacode && e.hw_sub_code == hw_sub_code)
        .or_else(|| entries.iter().find(|e| e.hw_code == dacode))
        .ok_or_else(|| format!("No DA entry for dacode 0x{dacode:04X}"))?;

    let region = if entry.regions.len() == 2 {
        entry.regions.get(1).ok_or("DA entry has no DA2 region")?
    } else {
        entry.regions.get(2).ok_or("DA entry has no DA2 region")?
    };

    let start = region.offset as usize;
    let content_len = (region.length - region.sig_len) as usize;
    let end = start + content_len;
    if end > data.len() {
        return Err("DA2 region out of bounds".into());
    }
    Ok((region.addr, data[start..end].to_vec()))
}

fn xflash_da2_boot(
    device: &mut MtkDevice,
    da_file: &[u8],
    dacode: u16,
    hw_sub_code: u16,
    hw_code: u16,
) -> Result<(), String> {
    let (da2_addr, da2) = select_da2(da_file, dacode, hw_sub_code)
        .map_err(|e| format!("select DA2: {e}"))?;

    let mut xf = XFlash::new(device);
    xf.da1_sync().map_err(|e| e.to_string())?;
    xf.get_packet_length().map_err(|e| e.to_string())?;
    xf.boot_to(da2_addr, &da2).map_err(|e| e.to_string())?;
    eprintln!("[dbg] boot_to ok, checking DA2...");
    match xf.get_packet_length() {
        Ok(()) => eprintln!("[dbg] get_packet_length after DA2 OK"),
        Err(e) => eprintln!("[dbg] get_packet_length after DA2 err: {e}"),
    }

    // Give DA2 a moment to initialize DRAM and storage.
    std::thread::sleep(Duration::from_millis(500));

    let storage_type = xf.detect_storage().map_err(|e| e.to_string())?;
    let gpt = xf.read_flash(0, 0x8000, storage_type).map_err(|e| e.to_string())?;

    let part_count = gpt_partition_count(&gpt);
    println!("searching for usb device... OK FOUND");
    println!("Reading partition information.... OK [{}]", part_count.unwrap_or(0));

    println!("Reading system information....");
    let chip = chip_name(hw_code);
    println!("Chipset type: {}", chip);

    let props = scan_system_props(&mut xf, storage_type, &gpt);
    println!(
        "Security Patch: {}",
        props.get("ro.build.version.security_patch").cloned().unwrap_or_default()
    );
    println!("Build Date: {}", props.get("ro.build.date").cloned().unwrap_or_default());
    println!("Build Number: {}", props.get("ro.build.id").cloned().unwrap_or_default());
    println!(
        "Incremental: {}",
        props.get("ro.build.version.incremental").cloned().unwrap_or_default()
    );
    println!("SDK Version: {}", props.get("ro.build.version.sdk").cloned().unwrap_or_default());
    println!(
        "Android Ver: {}",
        props.get("ro.build.version.release").cloned().unwrap_or_default()
    );
    println!();

    println!("Reading partition information.... OK [{}]", part_count.unwrap_or(0));
    println!("then reading other like system etc just type ... OK");

    Ok(())
}

fn gpt_partition_count(gpt: &[u8]) -> Option<u32> {
    let hdr = gpt.get(512..)?;
    if hdr.get(..8) != Some(b"EFI PART") {
        return None;
    }
    let num = u32::from_le_bytes(hdr.get(0x50..0x54)?.try_into().ok()?);
    Some(num)
}

fn scan_system_props(
    xf: &mut XFlash<'_, MtkDevice>,
    storage_type: u32,
    gpt: &[u8],
) -> std::collections::HashMap<String, String> {
    let mut props = std::collections::HashMap::new();
    if let Some((addr, size)) = find_system_partition(gpt) {
        eprintln!("scanning system partition @ 0x{addr:X} ({} MB)...", size / (1024 * 1024));
        let scan_len = size.min(64 * 1024 * 1024) as usize;
        if let Ok(data) = xf.read_flash(addr, scan_len, storage_type) {
            for key in [
                "ro.build.version.security_patch",
                "ro.build.date",
                "ro.build.id",
                "ro.build.version.incremental",
                "ro.build.version.sdk",
                "ro.build.version.release",
            ] {
                if let Some(v) = find_prop(&data, key) {
                    props.insert(key.to_string(), v);
                }
            }
        }
    }
    props
}

fn find_system_partition(gpt: &[u8]) -> Option<(u64, u64)> {
    let hdr = gpt.get(512..)?;
    if hdr.get(..8) != Some(b"EFI PART") {
        return None;
    }
    let entries_lba = u64::from_le_bytes(hdr.get(72..80)?.try_into().ok()?);
    let num_entries = u32::from_le_bytes(hdr.get(0x50..0x54)?.try_into().ok()?);
    let entry_size = u32::from_le_bytes(hdr.get(0x54..0x58)?.try_into().ok()?);
    if entry_size == 0 || entry_size > 0x200 {
        return None;
    }
    let start = entries_lba as usize * 512;
    let mut pos = start;
    for _ in 0..num_entries {
        if pos + entry_size as usize > gpt.len() {
            break;
        }
        let name_bytes = &gpt[pos + 56..pos + 56 + 72];
        let name = String::from_utf8_lossy(name_bytes)
            .trim_end_matches('\0')
            .trim_end_matches('\u{0}')
            .to_string();
        let type_guid = &gpt[pos..pos + 16];
        let first_lba = u64::from_le_bytes(gpt[pos + 32..pos + 40].try_into().ok()?);
        let last_lba = u64::from_le_bytes(gpt[pos + 40..pos + 48].try_into().ok()?);
        let size = (last_lba - first_lba + 1) * 512;
        if name.eq_ignore_ascii_case("system") && type_guid != [0u8; 16] {
            return Some((first_lba * 512, size));
        }
        pos += entry_size as usize;
    }
    None
}

fn find_prop(data: &[u8], key: &str) -> Option<String> {
    let needle = format!("{key}=");
    let idx = data.windows(needle.len()).position(|w| w == needle.as_bytes())?;
    let rest = &data[idx + needle.len()..];
    let end = rest.iter().position(|&b| b == b'\n').unwrap_or(rest.len());
    let val = String::from_utf8_lossy(&rest[..end]).trim().to_string();
    Some(val)
}

fn do_detect() -> Response {
    let devices = match nusb::list_devices().wait() {
        Ok(d) => d,
        Err(e) => return Response::error(format!("Failed to list USB devices: {e:?}")),
    };

    let mut found = Vec::new();
    for dev in devices {
        let vid = dev.vendor_id();
        let pid = dev.product_id();
        if let Some((_, _, ct)) = KNOWN_PORTS.iter().find(|(v, p, _)| *v == vid && *p == pid) {
            found.push(LocalDeviceInfo {
                vid,
                pid,
                mode: ct.as_str().to_string(),
                product: dev.product_string().unwrap_or("").to_string(),
                manufacturer: dev.manufacturer_string().unwrap_or("").to_string(),
            });
        }
    }

    if found.is_empty() {
        Response {
            status: "not_found".into(),
            devices: Some(vec![]),
            handshake: None,
            da_upload: None,
            error: None,
        }
    } else {
        Response {
            status: "ok".into(),
            devices: Some(found),
            handshake: None,
            da_upload: None,
            error: None,
        }
    }
}

fn do_connect(args: &[String]) -> Response {
    let wait = args.iter().any(|a| a == "--wait" || a == "-w");
    let da_path = args.iter().find(|a| !a.starts_with('-')).map(|s| s.clone());

    let mut device = if wait {
        match find_mtk_device_poll(Duration::from_secs(120)) {
            Some(dev) => dev,
            None => return Response::error("No MTK device found after 120s".to_string()),
        }
    } else {
        match find_and_open() {
            Ok(dev) => dev,
            Err(e) => return Response::error(e),
        }
    };
    let conn_type = device.conn_type();

    let mut proto = PlProtocol::new(&mut device);
    let hs_err = match proto.handshake() {
        Ok(()) => None,
        Err(e) => Some(e.to_string()),
    };

    let mut hw_code = None;
    let mut hw_sub_code = None;
    let mut hw_ver = None;
    let mut sw_ver = None;
    let mut bl_version = None;
    let mut target_config = None;
    let mut soc_id = None;
    let mut me_id = None;

    let mut da_success = false;
    let mut da_error = None;
    let mut da_address = None;
    let mut da_size = None;
    let mut da_sig_len = None;

    if hs_err.is_none() {
        if let Ok(c) = proto.get_hw_code() {
            hw_code = Some(c as u32);
        }
        if let Ok((sub, hw, sw)) = proto.get_hw_sw_ver() {
            hw_sub_code = Some(sub as u32);
            hw_ver = Some(hw as u32);
            sw_ver = Some(sw as u32);
        }
        if let Ok(c) = proto.get_target_config() {
            target_config = Some(c);
        }
        let bl = if conn_type == ConnType::Brom {
            proto.get_br_version()
        } else {
            proto.get_pl_version()
        };
        if let Ok(v) = bl {
            bl_version = Some(v as u32);
        }
        if let Ok(v) = proto.get_soc_id() {
            soc_id = v;
        }
        if let Ok(v) = proto.get_me_id() {
            me_id = v;
        }

        if let Some(path) = da_path.as_ref() {
            match std::fs::read(path) {
                Ok(data) => {
                    let hw = hw_code.unwrap_or(0) as u16;
                    let sub = hw_sub_code.unwrap_or(0) as u16;
                    let dacode = dacode_for(hw);
                    match select_da1(&data, dacode, sub) {
                        Ok((da_addr, da1, da_sig)) => {
                            da_address = Some(da_addr);
                            da_size = Some(da1.len());
                            da_sig_len = Some(da_sig);

                            match proto.send_da(da_addr, &da1, da_sig) {
                                Ok(()) => {
                                    match proto.jump_da(da_addr) {
                                        Ok(()) => {
                                            match xflash_da2_boot(
                                                &mut device,
                                                &data,
                                                dacode,
                                                sub,
                                                hw,
                                            ) {
                                                Ok(()) => {
                                                    da_success = true;
                                                }
                                                Err(e) => {
                                                    da_error = Some(format!("DA2 boot failed: {e}"));
                                                    eprintln!("DA2 boot failed: {e}");
                                                }
                                            }
                                        }
                                        Err(e) => {
                                            da_error = Some(format!("Jump DA failed: {e}"));
                                            eprintln!("Jump DA failed: {e}");
                                        }
                                    }
                                }
                                Err(e) => {
                                    da_error = Some(format!("Send DA failed: {e}"));
                                    eprintln!("Send DA failed: {e}");
                                }
                            }
                        }
                        Err(e) => {
                            da_error = Some(e);
                            eprintln!("{}", da_error.as_deref().unwrap());
                        }
                    }
                }
                Err(e) => {
                    da_error = Some(format!("Failed to read DA file: {e}"));
                    eprintln!("Failed to read DA file: {e}");
                }
            }
        }
    } else if wait {
        eprintln!("Handshake failed: {}", hs_err.as_deref().unwrap_or("?"));
    }

    device.close();

    let success = hs_err.is_none();
    Response {
        status: if success { "ok".into() } else { "error".into() },
        devices: None,
        handshake: Some(HandshakeResult {
            success,
            hw_code,
            hw_sub_code,
            hw_ver,
            sw_ver,
            bl_version,
            target_config,
            soc_id,
            me_id,
            connection_type: Some(conn_type.as_str().to_string()),
            error: hs_err.clone(),
        }),
        da_upload: if da_path.is_some() {
            Some(DaUploadResult {
                success: da_success,
                address: da_address,
                size: da_size,
                sig_len: da_sig_len,
                error: da_error,
            })
        } else {
            None
        },
        error: if success { None } else { Some("Handshake failed".into()) },
    }
}

fn parse_hex(s: &str) -> Option<u32> {
    let t = s.trim();
    if let Some(hex) = t.strip_prefix("0x").or_else(|| t.strip_prefix("0X")) {
        u32::from_str_radix(hex, 16).ok()
    } else {
        t.parse::<u32>().ok()
    }
}

fn chip_name(hw_code: u16) -> &'static str {
    match hw_code {
        0x0321 => "MT6735",
        0x0326 => "MT6755",
        0x0335 => "MT6737",
        0x0507 => "MT6779",
        0x0551 => "MT6768",
        0x0562 => "MT6761",
        0x0570 | 0x6580 => "MT6580",
        0x0571 => "MT6572",
        0x0588 => "MT6785",
        0x0600 => "MT6853",
        0x0688 | 0x6771 => "MT6771",
        0x0717 | 0x6765 => "MT6765",
        0x0766 => "MT6877",
        0x0788 => "MT6873",
        0x0813 => "MT6833",
        0x0816 => "MT6885",
        0x0886 => "MT6885",
        0x0989 => "MT6833 (Dimensity 700)",
        0x0996 => "MT6853 (Dimensity 720)",
        0x1209 => "MT6985",
        _ => "unknown",
    }
}

/// BROM hw_code -> DA selection code (dacode). Defaults to the hw_code itself.
fn dacode_for(hw_code: u16) -> u16 {
    match hw_code {
        0x0989 => 0x6833,
        0x0996 => 0x6853,
        0x0816 => 0x6885,
        _ => hw_code,
    }
}

struct DaRegion {
    offset: u32,
    length: u32,
    addr: u32,
    sig_len: u32,
}

struct DaEntry {
    hw_code: u16,
    hw_sub_code: u16,
    regions: Vec<DaRegion>,
}

fn parse_da(data: &[u8]) -> Result<Vec<DaEntry>, String> {
    if data.len() < 0x6C {
        return Err("DA file too short".into());
    }
    if &data[..18] != b"MTK_DOWNLOAD_AGENT" {
        return Err("Not a valid DA file (missing MTK_DOWNLOAD_AGENT)".into());
    }
    let version = u32::from_le_bytes(data[96..100].try_into().unwrap());
    let magic = u32::from_le_bytes(data[100..104].try_into().unwrap());
    if magic != 0x22668899 {
        return Err("Invalid DA file magic".into());
    }
    let da_count = u32::from_le_bytes(data[104..108].try_into().unwrap()) as usize;
    let entry_size = if version == 4 { 0xDC } else { 0xD8 };

    let mut entries = Vec::new();
    let mut pos = 0x6Cusize;
    for _ in 0..da_count {
        if pos + 20 > data.len() {
            break;
        }
        let mut off = pos;
        let r16 = |off: &mut usize| -> u16 {
            let v = u16::from_le_bytes(data[*off..*off + 2].try_into().unwrap());
            *off += 2;
            v
        };
        let r32 = |off: &mut usize| -> u32 {
            let v = u32::from_le_bytes(data[*off..*off + 4].try_into().unwrap());
            *off += 4;
            v
        };
        let _magic = r16(&mut off);
        let hw_code = r16(&mut off);
        let hw_sub_code = r16(&mut off);
        let _hw_ver = r16(&mut off);
        let _sw_ver = r16(&mut off);
        let _reserved = r16(&mut off);
        if version == 4 {
            let _feature_set = r32(&mut off);
        }
        let _entry_index = r16(&mut off);
        let region_count = r16(&mut off) as usize;

        let mut regions = Vec::new();
        for _ in 0..region_count {
            if off + 20 > data.len() {
                break;
            }
            let offset = r32(&mut off);
            let length = r32(&mut off);
            let addr = r32(&mut off);
            let _region_length = r32(&mut off);
            let sig_len = r32(&mut off);
            regions.push(DaRegion { offset, length, addr, sig_len });
        }
        entries.push(DaEntry { hw_code, hw_sub_code, regions });
        pos += entry_size;
    }
    Ok(entries)
}

fn select_da1(data: &[u8], dacode: u16, hw_sub_code: u16) -> Result<(u32, Vec<u8>, u32), String> {
    let entries = parse_da(data)?;
    if entries.is_empty() {
        return Err("No DA entries found in file".into());
    }
    let entry = entries
        .iter()
        .find(|e| e.hw_code == dacode && e.hw_sub_code == hw_sub_code)
        .or_else(|| entries.iter().find(|e| e.hw_code == dacode))
        .ok_or_else(|| {
            format!("No DA entry for dacode 0x{dacode:04X} (sub 0x{hw_sub_code:04X})")
        })?;

    let region = if entry.regions.len() == 2 {
        entry.regions.first().ok_or("DA entry has no regions")?
    } else {
        entry.regions.get(1).ok_or("DA entry has no DA1 region")?
    };

    let start = region.offset as usize;
    let end = start + region.length as usize;
    if end > data.len() {
        return Err(format!(
            "DA1 region out of bounds (offset 0x{start:X} len 0x{:X})",
            region.length
        ));
    }
    Ok((region.addr, data[start..end].to_vec(), region.sig_len))
}

fn find_mtk_device_poll(timeout: Duration) -> Option<MtkDevice> {
    let start = Instant::now();
    loop {
        if let Ok(dev) = find_and_open() {
            return Some(dev);
        }
        if start.elapsed() >= timeout {
            return None;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

fn do_da(args: &[String]) -> Response {
    let mut da_path = None;
    let mut addr_override = None;
    let mut siglen_override = None;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--addr" | "-a" => {
                if i + 1 < args.len() {
                    addr_override = parse_hex(&args[i + 1]);
                    i += 1;
                }
            }
            "--siglen" | "-s" => {
                if i + 1 < args.len() {
                    siglen_override = parse_hex(&args[i + 1]);
                    i += 1;
                }
            }
            _ if da_path.is_none() => da_path = Some(args[i].clone()),
            _ => {}
        }
        i += 1;
    }

    let da_path = match da_path {
        Some(p) => p,
        None => {
            return Response::error(
                "Usage: mtk-backend da <da.bin> [--addr 0x200000] [--siglen 0x1000]".into(),
            );
        }
    };

    let data = match std::fs::read(&da_path) {
        Ok(d) => d,
        Err(e) => return Response::error(format!("Failed to read DA file: {e}")),
    };

    let mut device = match find_and_open() {
        Ok(dev) => dev,
        Err(e) => return Response::error(e),
    };

    let mut proto = PlProtocol::new(&mut device);
    if let Err(e) = proto.handshake() {
        device.close();
        return Response::error(format!("Handshake failed: {e}"));
    }

    let hw_code = proto.get_hw_code().unwrap_or(0);
    let hw_sub_code = proto.get_hw_sw_ver().map(|(sub, _, _)| sub).unwrap_or(0);
    let dacode = dacode_for(hw_code);

    let (da_addr, da1, da_sig) = match select_da1(&data, dacode, hw_sub_code) {
        Ok(v) => v,
        Err(e) => {
            device.close();
            return Response::error(e);
        }
    };
    let address = addr_override.unwrap_or(da_addr);
    let sig_len = siglen_override.unwrap_or(da_sig);

    let upload_err = match proto.send_da(address, &da1, sig_len) {
        Ok(()) => match proto.jump_da(address) {
            Ok(()) => None,
            Err(e) => Some(format!("Jump DA failed: {e}")),
        },
        Err(e) => Some(format!("Send DA failed: {e}")),
    };

    device.close();

    let success = upload_err.is_none();
    Response {
        status: if success { "ok".into() } else { "error".into() },
        devices: None,
        handshake: None,
        da_upload: Some(DaUploadResult {
            success,
            address: Some(address),
            size: Some(da1.len()),
            sig_len: Some(sig_len),
            error: upload_err,
        }),
        error: if success { None } else { Some("DA upload failed".into()) },
    }
}

fn print_help() {
    println!("mtk-backend - MediaTek BROM/Preloader tool");
    println!();
    println!("Commands:");
    println!("  detect                  list MTK USB devices (brom/preloader/da)");
    println!("  connect [--wait] [da]   connect + handshake, optionally load DA");
    println!("  da <file> [--addr ..]   upload a DA file and jump to it");
    println!("  help                    show this help");
    println!("  exit                    quit");
    println!();
    println!("Tip: 'connect --wait MTK_AllInOne_DA.bin' = wait for device, then auto handshake + DA.");
}

fn tokenize(input: &str) -> Vec<String> {
    let mut tokens = Vec::new();
    let mut cur = String::new();
    let mut in_quotes = false;
    for c in input.chars() {
        match c {
            '"' => in_quotes = !in_quotes,
            ' ' if !in_quotes => {
                if !cur.is_empty() {
                    tokens.push(std::mem::take(&mut cur));
                }
            }
            _ => cur.push(c),
        }
    }
    if !cur.is_empty() {
        tokens.push(cur);
    }
    tokens
}

fn run_interactive() {
    print_help();
    println!();
    loop {
        print!("mtk> ");
        let _ = io::stdout().flush();

        let mut line = String::new();
        match io::stdin().read_line(&mut line) {
            Ok(0) => break,
            Ok(_) => {}
            Err(_) => break,
        }

        let line = line.trim();
        if line.is_empty() {
            continue;
        }

        let tokens = tokenize(line);
        let mut iter = tokens.into_iter();
        let cmd = iter.next().unwrap().to_lowercase();
        let rest: Vec<String> = iter.collect();

        match cmd.as_str() {
            "help" | "?" => print_help(),
            "exit" | "quit" | "q" => break,
            "detect" | "list" | "devices" => print_response(&do_detect()),
            "connect" | "handshake" | "info" => print_response(&do_connect(&rest)),
            "da" | "send-da" | "upload-da" => print_response(&do_da(&rest)),
            "cls" | "clear" => {
                if cfg!(windows) {
                    let _ = std::process::Command::new("cmd").args(["/C", "cls"]).status();
                }
            }
            _ => println!("Unknown command: {cmd} (try 'help')"),
        }
        println!();
    }
}

fn print_response(resp: &Response) {
    println!("{}", serde_json::to_string(resp).unwrap());
}

fn main() {
    env_logger::init();

    let args: Vec<String> = std::env::args().collect();
    let cmd = args.get(1).map(|s| s.as_str());

    match cmd {
        None => run_interactive(),
        Some("help") | Some("-h") | Some("--help") => print_help(),
        Some(c) => {
            let rest = &args[2..];
            let resp = match c {
                "detect" | "list" | "devices" => do_detect(),
                "connect" | "handshake" | "info" => do_connect(rest),
                "da" | "send-da" | "upload-da" => do_da(rest),
                _ => Response::error(format!("Unknown command: {c}")),
            };
            print_response(&resp);
        }
    }
}