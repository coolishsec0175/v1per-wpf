using System;

namespace v1per_wpf.MediaTek.Models
{
    public enum MtkDeviceState
    {
        Disconnected,
        Handshaking,
        Brom,
        Preloader,
        Da1Loaded,
        Da2Loaded,
        Error
    }

    [Flags]
    public enum TargetConfigFlags : uint
    {
        None = 0,
        SbcEnabled = 1 << 0,
        SlaEnabled = 1 << 1,
        DaaEnabled = 1 << 2,
        SwJtagEnabled = 1 << 3,
        EppEnabled = 1 << 4,
        CertRequired = 1 << 5,
        MemReadAuth = 1 << 6,
        MemWriteAuth = 1 << 7,
        CmdC8Blocked = 1 << 8
    }

    public enum DaMode
    {
        Legacy = 0,
        XFlash = 5,
        Xml = 6
    }

    public enum ChecksumAlgorithm
    {
        None,
        CRC16,
        CRC32,
        XOR
    }

    public enum EmmcPartitionType
    {
        Boot1 = 0,
        Boot2 = 1,
        User = 2,
        Rpmb = 3
    }

    public enum MtkProtocolType
    {
        Auto,
        Xml,
        XFlash
    }

    public static class BromCommands
    {
        public const byte HANDSHAKE_SEND = 0xA0;
        public const byte HANDSHAKE_RESPONSE = 0x5F;

        public const byte CMD_GET_BL_VER = 0x01;
        public const byte CMD_GET_TARGET_CONFIG = 0x02;
        public const byte CMD_GET_HW_CODE = 0x03;
        public const byte CMD_GET_HW_SW_VER = 0x08;
        public const byte CMD_GET_ME_ID = 0x09;
        public const byte CMD_GET_SOC_ID = 0x0A;
        public const byte CMD_READ32 = 0xD1;
        public const byte CMD_WRITE32 = 0xD2;
        public const byte CMD_WRITE16 = 0xD3;
        public const byte CMD_SEND_DA = 0xD7;
        public const byte CMD_JUMP_DA = 0xD8;
        public const byte CMD_SEND_CERT = 0xE0;
        public const byte CMD_GET_VERSION = 0xE1;
    }

    public static class BromStatus
    {
        public const ushort Success = 0x0000;
        public const ushort AuthRequired = 0x0010;
        public const ushort PreloaderAuth = 0x0011;
        public const ushort SlaRequired = 0x1D0D;
        public const ushort DaVerificationFailed = 0x7015;
        public const ushort DaSecurityError = 0x7017;
    }

    public static class BromHandshake
    {
        public const byte SEND = 0xA0;
        public const byte RESPONSE = 0x5F;
        public const byte CONT1 = 0x0A;
        public const byte EXPECT1 = 0xF5;
        public const byte CONT2 = 0x50;
        public const byte EXPECT2 = 0xAF;
        public const byte CONT3 = 0x05;
        public const byte EXPECT3 = 0xFA;
    }

    public static class MtkDataPacker
    {
        public static byte[] PackUInt16BE(ushort value)
        {
            return new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
        }

        public static byte[] PackUInt32BE(uint value)
        {
            return new byte[]
            {
                (byte)((value >> 24) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)(value & 0xFF)
            };
        }

        public static ushort UnpackUInt16BE(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        public static ushort UnpackUInt16LE(byte[] data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        public static uint UnpackUInt32BE(byte[] data, int offset)
        {
            return (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
                          (data[offset + 2] << 8) | data[offset + 3]);
        }

        public static uint UnpackUInt32LE(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }

    public static class BromErrorHelper
    {
        public static bool IsSuccess(ushort status) => status <= 0xFF;

        public static string GetErrorMessage(ushort status)
        {
            return status switch
            {
                0x7015 => "DA signature verification failed",
                0x7017 => "DA security error (DAA enabled)",
                0x7018 => "DA hash mismatch",
                0x7019 => "DA correlation error",
                0x701A => "Invalid DA",
                0x701B => "Platform error",
                0x701C => "Authentication failed",
                0x701D => "Invalid jump address",
                0x701E => "Invalid format",
                0x701F => "Invalid DA version",
                0x7020 => "Invalid flash image",
                0x7021 => "Invalid regions",
                0x7022 => "Invalid partition table",
                0x7023 => "Invalid address",
                0x7024 => "Invalid data",
                0x7025 => "Invalid ECC",
                0x7026 => "Invalid SB keys",
                0x7027 => "Secure boot enabled",
                0x7028 => "Invalid BMT",
                0x7029 => "Invalid region",
                0x702A => "Invalid image",
                0x702B => "Invalid token",
                0x702C => "Invalid IV",
                0x702D => "Invalid public key",
                0x702E => "Invalid hash",
                0x702F => "Invalid signature",
                0x7030 => "Invalid cert",
                0x7031 => "Invalid protocol",
                0x7032 => "Invalid command",
                0x7033 => "Invalid parameter",
                0x7034 => "Invalid buffer",
                0x7035 => "Invalid length",
                0x7036 => "Invalid address",
                0x7037 => "Invalid mode",
                0x7038 => "Invalid type",
                0x7039 => "Invalid version",
                0x703A => "Invalid CRC",
                0x703B => "Invalid checksum",
                0x703C => "Invalid magic",
                0x703D => "Invalid header",
                0x703E => "Invalid data",
                0x703F => "Invalid status",
                0x7040 => "Invalid response",
                0x7041 => "Invalid timeout",
                0x7042 => "Invalid state",
                0x7043 => "Invalid operation",
                0x7044 => "Invalid device",
                0x7045 => "Invalid channel",
                0x7046 => "Invalid target",
                0x7047 => "Invalid source",
                0x7048 => "Invalid destination",
                0x7049 => "Invalid size",
                0x704A => "Invalid offset",
                0x704B => "Invalid alignment",
                0x704C => "Invalid access",
                0x704D => "Invalid permission",
                0x704E => "Invalid resource",
                0x704F => "Invalid configuration",
                _ => $"Unknown error: 0x{status:X4}"
            };
        }
    }
}
