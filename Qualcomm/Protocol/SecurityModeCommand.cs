using System.Runtime.InteropServices;

namespace v1per_wpf.Qualcomm;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SecurityModeCommand
{
	public byte uCommand;

	public byte uMode;
}
