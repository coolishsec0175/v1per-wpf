using System.Runtime.InteropServices;

namespace v1per_wpf.Qualcomm;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct OpenMultiImageResponse
{
	public byte uResponse;

	public byte uStatus;
}
