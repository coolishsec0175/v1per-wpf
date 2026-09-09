using System;
using System.Collections.Generic;
using System.Linq;

namespace v1per_wpf.Qualcomm;

/// <summary>Detects Qualcomm EDL (9008) COM ports via the shared SetupAPI enumeration.</summary>
public class ComPortCtrl
{
    public static string[] getDevicesQc()
    {
        var list = new List<string>();
        try
        {
            foreach (var port in V1Per.ComPorts.Enumerate())
            {
                string desc = (port.Description + " ").ToLowerInvariant();
                if (desc.Contains("qdloader 9008") || desc.Contains("qualcomm"))
                    list.Add(port.Device);
            }
        }
        catch (Exception ex)
        {
            Log.w(ex.Message);
        }
        return list.Distinct().ToArray();
    }
}