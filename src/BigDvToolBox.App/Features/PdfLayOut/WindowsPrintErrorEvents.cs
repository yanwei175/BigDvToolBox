using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>只读 PrintService/Admin；不启用日志、不修改打印队列、不需要额外 NuGet。</summary>
internal static class WindowsPrintErrorEvents
{
    public static IReadOnlyList<string> Read(DateTimeOffset since, string taskName)
    {
        string query = $"*[System[(Level <= 3) and TimeCreated[@SystemTime >= '{since.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ}']]]";
        nint results = EvtQuery(0, "Microsoft-Windows-PrintService/Admin", query, 1 | 0x200);
        if (results == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var matched = new List<string>();
        try
        {
            var handles = new nint[1];
            // 有界快照，避免后台诊断占用太久。
            for (int count = 0; count < 256; count++)
            {
                if (!EvtNext(results, 1, handles, 0, 0, out _))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new Win32Exception(error);
                }
                try
                {
                    EvtRender(0, handles[0], 1, 0, null, out int bytes, out _);
                    int error = Marshal.GetLastWin32Error();
                    if (error != 122) throw new Win32Exception(error);
                    var xml = new StringBuilder(bytes / 2);
                    if (!EvtRender(0, handles[0], 1, bytes, xml, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (MatchesTask(xml.ToString(), taskName)) matched.Add(xml.ToString());
                }
                finally { EvtClose(handles[0]); }
            }
        }
        finally { EvtClose(results); }
        return matched;
    }

    internal static bool MatchesTask(string xml, string taskName)
    {
        var root = XElement.Parse(xml);
        // 使用解析后的文本匹配，兼容任务名中被 XML 转义的字符；不采集其他应用的任务。
        return root.Descendants().Any(e => !e.HasElements && e.Value.Contains(taskName, StringComparison.Ordinal));
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint EvtQuery(nint session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(nint result, int arraySize, [Out] nint[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(nint context, nint fragment, int flags, int bufferSize, StringBuilder? buffer, out int used, out int count);
    [DllImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(nint handle);
}
