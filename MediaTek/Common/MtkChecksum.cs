using System;

namespace v1per_wpf.MediaTek.Common
{
    public static class MtkChecksum
    {
        public static ushort CalculateXorChecksum(byte[] data, int offset, int length)
        {
            ushort checksum = 0;
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                checksum ^= data[i];
            }
            return checksum;
        }

        public static ushort CalculateAddChecksum(byte[] data, int offset, int length)
        {
            ushort checksum = 0;
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        }

        public static uint CalculateCrc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ 0xEDB88320;
                    else
                        crc >>= 1;
                }
            }
            return crc ^ 0xFFFFFFFF;
        }

        public static (ushort checksum, byte[] processedData) PrepareData(
            byte[] dataWithoutSig, byte[]? signature, int dataLen)
        {
            int totalLen = dataLen + (signature?.Length ?? 0);
            byte[] processed = new byte[totalLen];
            Array.Copy(dataWithoutSig, 0, processed, 0, dataLen);
            if (signature != null)
                Array.Copy(signature, 0, processed, dataLen, signature.Length);

            ushort checksum = CalculateXorChecksum(processed, 0, totalLen);
            return (checksum, processed);
        }
    }
}
