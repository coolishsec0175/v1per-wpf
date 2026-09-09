using System.Collections;

namespace v1per_wpf.Qualcomm;

public class SoftwareImage
{
	public static string ProgrammerPattern => "prog_.*_firehose_.*.*|prog_.*firehose_.*.*|prog_firehose_*.elf|xbl_.*devprg_.*.*";

	public static string MTKDAPattern => "MTK_AllInOne_DA+\\w*.bin";

	public static string ProgrammerLite => "prog_firehose_lite.elf";

	public static string ProgrammerDDR4 => "prog_ufs_firehose_sm8250_ddr_4.elf";

	public static string ProgrammerDDR5 => "prog_ufs_firehose_sm8250_ddr_5.elf";

	public static string BootImage => "*_msimage.mbn";

	public static string ProvisionPattern => "provision.*\\.xml";

	public static string RawBackupPattern => "rawprogram_backup.xml";

	public static string RawRestorePattern => "rawprogram_restore.xml";

	public static string RawProgramPattern => "rawprogram[0-9]{1,20}\\.xml";

	public static string PatchPattern => "patch[0-9]{1,20}\\.xml";

	// Snapdragon 8 Series (Flagship)
	public static string SD845 => "prog_ufs_firehose_sdm845.elf";
	public static string SD855 => "prog_ufs_firehose_sm8150.elf";
	public static string SD855Plus => "prog_ufs_firehose_sm8150_ddr5.elf";
	public static string SD860 => "prog_ufs_firehose_sm8150.elf";
	public static string SD865 => "prog_ufs_firehose_sm8250.elf";
	public static string SD870 => "prog_ufs_firehose_sm8250.elf";
	public static string SD888 => "prog_ufs_firehose_sm8350.elf";
	public static string SD888Plus => "prog_ufs_firehose_sm8350.elf";
	public static string SD8Gen1 => "prog_ufs_firehose_sm8450.elf";
	public static string SD8Gen1Plus => "prog_ufs_firehose_sm8475.elf";
	public static string SD8Gen2 => "prog_ufs_firehose_sm8550.elf";
	public static string SD8Gen3 => "prog_ufs_firehose_sm8650.elf";
	public static string SD8Elite => "prog_ufs_firehose_sm8750.elf";

	// Snapdragon 7 Series (Upper Mid-Range)
	public static string SD710 => "prog_ufs_firehose_sd710.elf";
	public static string SD720G => "prog_ufs_firehose_trinket.elf";
	public static string SD730 => "prog_ups_firehose_sd730.elf";
	public static string SD730G => "prog_ufs_firehose_sd730.elf";
	public static string SD750G => "prog_ufs_firehose_lito.elf";
	public static string SD765G => "prog_ufs_firehose_lito.elf";
	public static string SD768G => "prog_ufs_firehose_lito.elf";
	public static string SD778G => "prog_ufs_firehose_sm7325.elf";
	public static string SD778GPlus => "prog_ufs_firehose_sm7325.elf";
	public static string SD780G => "prog_ufs_firehose_sm7325.elf";
	public static string SD7Gen1 => "prog_ufs_firehose_sm7450.elf";
	public static string SD7Gen2 => "prog_ufs_firehose_sm7475.elf";
	public static string SD7sGen2 => "prog_ufs_firehose_sm7550.elf";
	public static string SD7Gen3 => "prog_ufs_firehose_sm7635.elf";
	public static string SD7PlusGen2 => "prog_ufs_firehose_sm7475.elf";
	public static string SD7PlusGen3 => "prog_ufs_firehose_sm7635.elf";

	// Snapdragon 6 Series (Mid-Range)
	public static string SD636 => "prog_ufs_firehose_trinket.elf";
	public static string SD665 => "prog_ufs_firehose_trinket.elf";
	public static string SD670 => "prog_ufs_firehose_trinket.elf";
	public static string SD675 => "prog_ufs_firehose_trinket.elf";
	public static string SD680 => "prog_ufs_firehose_trinket.elf";
	public static string SD685 => "prog_ufs_firehose_trinket.elf";
	public static string SD695 => "prog_ufs_firehose_sm6375.elf";
	public static string SD6Gen1 => "prog_ufs_firehose_sm6450.elf";
	public static string SD6sGen3 => "prog_ufs_firehose_sm6650.elf";
	public static string SD6Gen3 => "prog_ufs_firehose_sm6650.elf";

	// Snapdragon 4 Series (Entry-Level)
	public static string SD460 => "prog_ufs_firehose_trinket.elf";
	public static string SD480 => "prog_ufs_firehose_trinket.elf";
	public static string SD4Gen1 => "prog_ufs_firehose_sm4375.elf";
	public static string SD4Gen2 => "prog_ufs_firehose_sm4450.elf";
	public static string SD4sGen2 => "prog_ufs_firehose_sm4450.elf";
	public static string SD4Gen3 => "prog_ufs_firehose_sm4650.elf";

	// Snapdragon 2 Series (Budget)
	public static string SD2Gen1 => "prog_ufs_firehose_sm2150.elf";

	// Snapdragon W Series (Wearables)
	public static string SW5100 => "prog_ufs_firehose_sw5100.elf";

	// Generic patterns for all firehose programmers
	public static string GenericFirehosePattern => "prog_.*_firehose_.*\\.elf";
	public static string GenericUFSFirehosePattern => "prog_ufs_firehose_.*\\.elf";
	public static string GenericEMMCFirehosePattern => "prog_emmc_firehose_.*\\.elf";
	public static string XBLDevprgPattern => "xbl_.*devprg_.*\\.elf";

	public static Hashtable DummyProgress => new Hashtable
	{
		{ "xbl", 1 },
		{ "tz", 2 },
		{ "hyp", 3 },
		{ "rpm", 4 },
		{ "emmc_appsboot", 5 },
		{ "pmic", 6 },
		{ "devcfg", 7 },
		{ "BTFM", 8 },
		{ "cmnlib", 9 },
		{ "cmnlib64", 10 },
		{ "NON-HLOS", 11 },
		{ "adspso", 12 },
		{ "mdtp", 13 },
		{ "keymaster", 14 },
		{ "misc", 15 },
		{ "system", 16 },
		{ "cache", 30 },
		{ "userdata", 34 },
		{ "recovery", 35 },
		{ "splash", 36 },
		{ "logo", 37 },
		{ "boot", 38 },
		{ "cust", 45 }
	};
}
