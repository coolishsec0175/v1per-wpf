use nusb::descriptors::TransferType;
use nusb::io::{EndpointRead, EndpointWrite};
use nusb::transfer::{Bulk, ControlOut, ControlType, Direction, In, Out, Recipient};
use nusb::{Device, DeviceInfo, Interface, MaybeFuture};
use serde::Serialize;
use std::io::{self, Read, Write};
use std::time::{Duration, Instant};

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

        self.ctrl_interface = Some(
            device
                .detach_and_claim_interface(ctrl_num)
                .wait()
                .map_err(|e| format!("Failed to claim control interface: {e:?}"))?,
        );
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
}

struct PlProtocol<'a> {
    port: &'a mut UsbMtkPort,
}

impl<'a> PlProtocol<'a> {
    fn new(port: &'a mut UsbMtkPort) -> Self {
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

    if wait {
        eprintln!("Waiting for MTK devices...");
        eprintln!("Power off the phone, hold ALL buttons, then plug USB.");
    }

    let (info, conn_type) = match if wait {
        match find_mtk_device_poll(Duration::from_secs(120)) {
            Some(v) => Ok(v),
            None => Err("No MTK device found after 120s".to_string()),
        }
    } else {
        UsbMtkPort::find_mtk_device()
    } {
        Ok(v) => v,
        Err(e) => return Response::error(e),
    };

    if wait {
        eprintln!(
            "OK FOUND: {:04X}:{:04X} ({})",
            info.vendor_id(),
            info.product_id(),
            conn_type.as_str()
        );
    }

    let mut port = UsbMtkPort::new(info, conn_type);
    if let Err(e) = port.open() {
        return Response::error(e);
    }

    let mut proto = PlProtocol::new(&mut port);
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

        if wait {
            eprintln!(
                "Handshake OK. Chip: {} (hw_code 0x{:04X})",
                chip_name(hw_code.unwrap_or(0) as u16),
                hw_code.unwrap_or(0)
            );
        }

        if let Some(path) = da_path.as_ref() {
            match std::fs::read(&path) {
                Ok(data) => {
                    let hw = hw_code.unwrap_or(0) as u16;
                    let address = da1_address(hw);
                    let sig_len = detect_signature_length(&data, hw) as u32;
                    da_address = Some(address);
                    da_size = Some(data.len());
                    da_sig_len = Some(sig_len);

                    if wait {
                        eprintln!(
                            "Loading DA: {} ({} bytes @ 0x{:08X}, sig_len 0x{:X})",
                            path,
                            data.len(),
                            address,
                            sig_len
                        );
                    }
                    match proto.send_da(address, &data, sig_len) {
                        Ok(()) => {
                            if wait {
                                eprintln!("DA sent, jumping to 0x{:08X}...", address);
                            }
                            match proto.jump_da(address) {
                                Ok(()) => {
                                    da_success = true;
                                    if wait {
                                        eprintln!("DA booted! Device will re-enumerate as DA.");
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
                    da_error = Some(format!("Failed to read DA file: {e}"));
                    eprintln!("Failed to read DA file: {e}");
                }
            }
        }
    } else if wait {
        eprintln!("Handshake failed: {}", hs_err.as_deref().unwrap_or("?"));
    }

    port.close();

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
        0x0886 => "MT6885",
        0x0989 => "MT6891",
        0x0996 => "MT6895",
        0x1209 => "MT6985",
        _ => "unknown",
    }
}

fn find_mtk_device_poll(timeout: Duration) -> Option<(DeviceInfo, ConnType)> {
    let start = Instant::now();
    loop {
        if let Ok((info, ct)) = UsbMtkPort::find_mtk_device() {
            return Some((info, ct));
        }
        if start.elapsed() >= timeout {
            return None;
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

fn da1_address(hw_code: u16) -> u32 {
    // All chips in the reference database load DA1 at 0x200000.
    let _ = hw_code;
    0x200000
}

fn default_signature_length(hw_code: u16) -> usize {
    let _ = hw_code;
    0x1000
}

fn detect_signature_length(data: &[u8], hw_code: u16) -> usize {
    if data.len() >= 4 && data[0] == 0x7F && data[1] == b'E' && data[2] == b'L' && data[3] == b'F' {
        return default_signature_length(hw_code);
    }
    if data.len() >= 4 && (data[3] == 0xEA || data[3] == 0xEB) {
        return 0x100;
    }
    if data.len() >= 8 {
        let header = &data[..8];
        if header.windows(4).any(|w| w == b"MTK") || header.windows(4).any(|w| w == b"hvea") {
            return 0x1000;
        }
    }
    default_signature_length(hw_code)
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

    let (info, conn_type) = match UsbMtkPort::find_mtk_device() {
        Ok(v) => v,
        Err(e) => return Response::error(e),
    };

    let mut port = UsbMtkPort::new(info, conn_type);
    if let Err(e) = port.open() {
        return Response::error(e);
    }

    let mut proto = PlProtocol::new(&mut port);
    if let Err(e) = proto.handshake() {
        port.close();
        return Response::error(format!("Handshake failed: {e}"));
    }

    let hw_code = proto.get_hw_code().unwrap_or(0);
    let address = addr_override.unwrap_or_else(|| da1_address(hw_code));
    let sig_len = siglen_override.unwrap_or(detect_signature_length(&data, hw_code) as u32);

    let upload_err = match proto.send_da(address, &data, sig_len) {
        Ok(()) => match proto.jump_da(address) {
            Ok(()) => None,
            Err(e) => Some(format!("Jump DA failed: {e}")),
        },
        Err(e) => Some(format!("Send DA failed: {e}")),
    };

    port.close();

    let success = upload_err.is_none();
    Response {
        status: if success { "ok".into() } else { "error".into() },
        devices: None,
        handshake: None,
        da_upload: Some(DaUploadResult {
            success,
            address: Some(address),
            size: Some(data.len()),
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

        let mut parts = line.split_whitespace();
        let cmd = parts.next().unwrap().to_lowercase();
        let rest: Vec<String> = parts.map(String::from).collect();

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