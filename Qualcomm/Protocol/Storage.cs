using System.Runtime.InteropServices;

namespace v1per_wpf.Qualcomm;

[StructLayout(LayoutKind.Sequential, Size = 1)]
public struct Storage
{
	public static string ufs = "ufs";

	public static string emmc = "emmc";
}
