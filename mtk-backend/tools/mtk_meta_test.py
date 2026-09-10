import serial
import serial.tools.list_ports
import struct
import sys
import time


def find_meta_port():
    # META uses the modem/AT port. Look for common MTK modem ports.
    for p in serial.tools.list_ports.comports():
        if p.vid == 0x0E8D or "MTK" in (p.product or "") or "MediaTek" in (p.manufacturer or ""):
            yield p.device, p


def frame(code, payload):
    # [0x7E][field u32][len u16 = len(payload)+8][code u16][payload][0x7E]
    buf = bytearray()
    buf.append(0x7E)
    buf += struct.pack("<I", 0)
    buf += struct.pack("<H", len(payload) + 8)
    buf += struct.pack("<H", code)
    buf += payload
    buf.append(0x7E)
    return bytes(buf)


def read_frame(s, timeout=10):
    # Read: 0x7E, u32 field, u16 len, u16 code, payload(len-8), 0x7E
    s.timeout = timeout
    start = s.read(1)
    if not start or start[0] != 0x7E:
        return None
    field = s.read(4)
    if len(field) != 4:
        return None
    ln = s.read(2)
    if len(ln) != 2:
        return None
    length = struct.unpack("<H", ln)[0]
    code = s.read(2)
    if len(code) != 2:
        return None
    plen = length - 8 if length >= 8 else 0
    payload = s.read(plen) if plen else b""
    end = s.read(1)
    return struct.unpack("<H", code)[0], payload


def getprop(s, prop):
    cmd = ('AT+SHELL="getprop %s"' % prop).encode()
    s.write(frame(0x68, cmd))
    resp = read_frame(s)
    if resp:
        code, payload = resp
        return payload
    return None


PROPS = [
    "ro.build.version.release",
    "ro.build.version.sdk",
    "ro.build.version.security_patch",
    "ro.build.version.incremental",
    "ro.build.id",
    "ro.build.date",
    "ro.product.brand",
    "ro.product.model",
    "ro.product.name",
    "ro.board.platform",
]


def main():
    if len(sys.argv) > 1:
        port = sys.argv[1]
        s = serial.Serial(port, 115200, timeout=10)
    else:
        found = None
        for dev, p in find_meta_port():
            print("trying", dev, p.product)
            found = dev
            break
        if not found:
            print("no MTK/modem port found")
            return
        s = serial.Serial(found, 115200, timeout=10)

    for prop in PROPS:
        val = getprop(s, prop)
        print("%s=%s" % (prop, val.decode(errors="ignore").strip() if val else "<none>"))
        time.sleep(0.2)

    s.close()


if __name__ == "__main__":
    main()