use nusb::descriptors::TransferType;
use nusb::io::{EndpointRead, EndpointWrite};
use nusb::transfer::{Bulk, ControlOut, ControlType, Direction, In, Out, Recipient};
use nusb::{Device, DeviceInfo, Interface, MaybeFuture};
use serde::Serialize;
use std::io::{self, Read, Write};
use std::time::Duration;

const MTK_VID: u16 = 0x0E8D;
const KNOWN_PORTS: &[(u16, u16, &str)] = &[
    (0x0E8D, 0x0003, "brom"),
    (0x0E8D, 0x6000, "preloader"),
    (0x0E8D, 0x2000, "preloader"),
    (0x0E8D, 0x2001, "da"),
    (0x0E8D, 0x20FF, "preloader"),
    (0x0E8D, 0x3000, "preloader"),
];

const HANDSHAKE_SEQ: [u8; 4] = [0xA0, 0x0A, 0x50, 0x05];
const BULK_IN_SZ: usize = 0x80000;
const BULK_OUT_SZ: usize = 0x80000;

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
    hw_code: Option<u32>,
    hw_sub_code: Option<u32>,
    bl_version: Option<u32>,
    target_config: Option<u32>,
    error: Option<String>,
}

#[derive(Serialize)]
struct DaUploadResult {
    success: bool,
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

fn find_cdc_interfaces(device: &Device) -> Result<(u8, u8), String> {
    let mut ctrl_num: Option<u8> = None;
    let mut bulk_num: Option<u8> = None;

    for config in device.configurations() {
        for iface in config.interfaces() {
            for alt in iface.alt_settings() {
                match alt.class() {
                    2 => ctrl_num = Some(alt.interface_number()),
                    10 => bulk_num = Some(alt.interface_number()),
                    _ => {}
                }
            }
        }
    }

    match (ctrl_num, bulk_num) {
        (Some(c), Some(b)) => Ok((c, b)),
        _ => Err("CDC interfaces not found".into()),
    }
}

fn select_bulk_endpoints(iface: &Interface) -> Result<(u8, u8, usize, usize), String> {
    for alt in iface.descriptors() {
        let mut in_ep: Option<u8> = None;
        let mut out_ep: Option<u8> = None;
        let mut in_mps = 0usize;
        let mut out_mps = 0usize;

        for ep in alt.endpoints() {
            if !matches!(ep.transfer_type(), TransferType::Bulk) {
                continue;
            }
            match ep.direction() {
                Direction::In => {
                    in_ep = Some(ep.address());
                    in_mps = ep.max_packet_size();
                }
                Direction::Out => {
                    out_ep = Some(ep.address());
                    out_mps = ep.max_packet_size();
                }
            }
        }

        if let (Some(i), Some(o)) = (in_ep, out_ep) {
            return Ok((i, o, in_mps, out_mps));
        }
    }
    Err("No bulk endpoints found".into())
}

fn setup_cdc(ctrl_iface: &Interface) -> Result<(), String> {
    const SET_LINE_CODING: u8 = 0x20;
    const SET_CONTROL_LINE_STATE: u8 = 0x22;
    const LINE_CODING: [u8; 7] = [0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x08];
    const DTR_RTS: u16 = 0x03;

    ctrl_iface
        .control_out(
            ControlOut {
                control_type: ControlType::Class,
                recipient: Recipient::Interface,
                request: SET_LINE_CODING,
                value: 0,
                index: 0,
                data: &LINE_CODING,
            },
            Duration::from_secs(1),
        )
        .wait()
        .map_err(|e| format!("SET_LINE_CODING failed: {:?}", e))?;

    ctrl_iface
        .control_out(
            ControlOut {
                control_type: ControlType::Class,
                recipient: Recipient::Interface,
                request: SET_CONTROL_LINE_STATE,
                value: DTR_RTS,
                index: 0,
                data: &[],
            },
            Duration::from_secs(1),
        )
        .wait()
        .map_err(|e| format!("SET_CONTROL_LINE_STATE failed: {:?}", e))?;

    Ok(())
}

fn do_detect() -> Response {
    let devices = match nusb::list_devices().wait() {
        Ok(devs) => devs,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to list USB devices: {:?}", e)),
            };
        }
    };

    let mut found = Vec::new();
    for dev in devices {
        let vid = dev.vendor_id();
        let pid = dev.product_id();

        let mode = KNOWN_PORTS
            .iter()
            .find(|(v, p, _)| *v == vid && *p == pid)
            .map(|(_, _, m)| m.to_string());

        if vid == MTK_VID || mode.is_some() {
            let product = dev.product_string().unwrap_or("").to_string();
            let manufacturer = dev.manufacturer_string().unwrap_or("").to_string();
            found.push(LocalDeviceInfo {

                vid,
                pid,
                mode: mode.unwrap_or_else(|| "unknown".into()),
                product,
                manufacturer,
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

fn do_connect() -> Response {
    let device_info = match nusb::list_devices().wait() {
        Ok(devs) => {
            let mut found = None;
            for dev in devs {
                if dev.vendor_id() == MTK_VID {
                    found = Some(dev);
                    break;
                }
            }
            found
        }
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to list USB devices: {:?}", e)),
            };
        }
    };

    let device_info = match device_info {
        Some(d) => d,
        None => {
            return Response {
                status: "not_found".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some("No MTK device found".into()),
            };
        }
    };

    let device = match device_info.open().wait() {
        Ok(d) => d,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to open device: {:?}", e)),
            };
        }
    };

    let (ctrl_num, bulk_num) = match find_cdc_interfaces(&device) {
        Ok(v) => v,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(e),
            };
        }
    };

    let ctrl_iface = match device.detach_and_claim_interface(ctrl_num).wait() {
        Ok(i) => i,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to claim control interface: {:?}", e)),
            };
        }
    };

    let bulk_iface = match device.detach_and_claim_interface(bulk_num).wait() {
        Ok(i) => i,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to claim bulk interface: {:?}", e)),
            };
        }
    };

    let (ep_in, ep_out, _in_mps, _out_mps) = match select_bulk_endpoints(&bulk_iface) {

        Ok(v) => v,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(e),
            };
        }
    };

    let _ = setup_cdc(&ctrl_iface);

    let tr = if cfg!(windows) { 1 } else { 8 };

    let mut writer = match bulk_iface
        .endpoint::<Bulk, Out>(ep_out)
        .and_then(|ep| ep.writer(BULK_OUT_SZ).with_num_transfers(tr).with_write_timeout(Duration::from_secs(5)))
        .build()
    {
        Ok(w) => w,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to create writer: {:?}", e)),
            };
        }
    };

    let mut reader = match bulk_iface
        .endpoint::<Bulk, In>(ep_in)
        .and_then(|ep| ep.reader(BULK_IN_SZ).with_num_transfers(tr).with_read_timeout(Duration::from_secs(5)))
        .build()
    {
        Ok(r) => r,
        Err(e) => {
            return Response {
                status: "error".into(),
                devices: None,
                handshake: None,
                da_upload: None,
                error: Some(format!("Failed to create reader: {:?}", e)),
            };
        }
    };

    let mut hw_code = None;
    let mut hw_sub_code = None;
    let mut bl_version = None;
    let mut target_config = None;

    let mut handshake_ok = true;

    for &send_byte in &HANDSHAKE_SEQ {
        let expected = send_byte ^ 0xFF;

        if writer.write_all(&[send_byte]).is_err() {
            handshake_ok = false;
            break;
        }

        let mut resp = [0u8; 1];
        match reader.read_exact(&mut resp) {
            Ok(()) => {}
            Err(e) => {
                if e.kind() == io::ErrorKind::TimedOut {
                    let _ = writer.write_all(&[send_byte]);
                    match reader.read_exact(&mut resp) {
                        Ok(()) => {}
                        Err(_) => {
                            handshake_ok = false;
                            break;
                        }
                    }
                } else {
                    handshake_ok = false;
                    break;
                }
            }
        }

        if resp != expected {

            handshake_ok = false;
            break;
        }
    }

    if handshake_ok {
        let _ = writer.write_all(&[0xA0; 20]);

        let mut buf = [0u8; 4];
        if reader.read_exact(&mut buf).is_ok() {
            hw_code = Some(u32::from_le_bytes(buf));
        }
        if reader.read_exact(&mut buf).is_ok() {
            hw_sub_code = Some(u32::from_le_bytes(buf));
        }
        if reader.read_exact(&mut buf).is_ok() {
            bl_version = Some(u32::from_le_bytes(buf));
        }
        if reader.read_exact(&mut buf).is_ok() {
            target_config = Some(u32::from_le_bytes(buf));
        }
    }

    Response {
        status: if handshake_ok { "ok".into() } else { "error".into() },
        devices: None,
        handshake: Some(HandshakeResult {
            success: handshake_ok,
            hw_code,
            hw_sub_code,
            bl_version,
            target_config,
            error: if handshake_ok { None } else { Some("Handshake failed".into()) },
        }),
        da_upload: None,
        error: if handshake_ok { None } else { Some("Handshake failed".into()) },
    }
}

fn print_response(resp: &Response) {
    println!("{}", serde_json::to_string(resp).unwrap());
}

fn main() {
    env_logger::init();

    let args: Vec<String> = std::env::args().collect();
    let cmd = args.get(1).map(|s| s.as_str()).unwrap_or("detect");

    let resp = match cmd {
        "detect" => do_detect(),
        "connect" => do_connect(),
        _ => Response {
            status: "error".into(),
            devices: None,
            handshake: None,
            da_upload: None,
            error: Some(format!("Unknown command: {}", cmd)),
        },
    };

    print_response(&resp);
}
