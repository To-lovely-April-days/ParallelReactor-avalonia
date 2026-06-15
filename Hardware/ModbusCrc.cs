using System;

namespace ParallelReactor.Hardware;

/// <summary>Modbus RTU 的 CRC-16 校验（多项式 0xA001，低字节在前）。</summary>
public static class ModbusCrc
{
    public static ushort Compute(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }

    /// <summary>在 PDU 末尾追加 CRC（低字节在前），返回含校验的完整帧。</summary>
    public static byte[] Append(byte[] pdu)
    {
        ushort crc = Compute(pdu, 0, pdu.Length);
        var frame = new byte[pdu.Length + 2];
        Array.Copy(pdu, frame, pdu.Length);
        frame[pdu.Length] = (byte)(crc & 0xFF);
        frame[pdu.Length + 1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>校验一帧（最后两字节为 CRC，低字节在前）。</summary>
    public static bool Validate(byte[] frame, int length)
    {
        if (length < 4) return false;
        ushort calc = Compute(frame, 0, length - 2);
        ushort recv = (ushort)(frame[length - 2] | (frame[length - 1] << 8));
        return calc == recv;
    }
}
