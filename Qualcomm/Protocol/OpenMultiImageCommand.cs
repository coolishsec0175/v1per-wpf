using System.Runtime.InteropServices;

namespace v1per_wpf.Qualcomm;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct OpenMultiImageCommand
{
	public byte uCommand;

	public byte uType;

	[MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
	public byte[] uData;
}
