using System.Runtime.InteropServices;
using System.Text;

namespace HazelInvoice.Services.Printing;

internal static class Win32PrinterApi
{
    private const uint PRINTER_ENUM_LOCAL = 0x00000002;
    private const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct PRINTER_INFO_4
    {
        public IntPtr pPrinterName;
        public IntPtr pServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr pPrinterEnum,
        uint cbBuf,
        out uint pcbNeeded,
        out uint pcReturned);

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOC_INFO_1
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDatatype;

        public DOC_INFO_1(string docName, string datatype)
        {
            pDocName = docName;
            pOutputFile = null;
            pDatatype = datatype;
        }
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] ref DOC_INFO_1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    public static IReadOnlyList<string> GetInstalledPrinterNames()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<string>();

        var flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        const uint level = 4;

        if (!EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out var returned) && needed == 0)
            return Array.Empty<string>();

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(flags, null, level, buffer, needed, out _, out returned))
                return Array.Empty<string>();

            var names = new List<string>((int)returned);
            var offset = buffer;
            var size = Marshal.SizeOf<PRINTER_INFO_4>();

            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(offset);
                var name = Marshal.PtrToStringUni(info.pPrinterName);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
                offset = IntPtr.Add(offset, size);
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x)
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool TryRawPrint(string printerName, string documentName, string text, out string? error)
    {
        error = null;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            error = "RAW printing is only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(printerName))
        {
            error = "Printer name is required.";
            return false;
        }

        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero) || hPrinter == IntPtr.Zero)
        {
            error = $"Unable to open printer (Win32Error={Marshal.GetLastWin32Error()}).";
            return false;
        }

        try
        {
            var docInfo = new DOC_INFO_1(documentName, "RAW");
            var jobId = StartDocPrinter(hPrinter, 1, ref docInfo);
            if (jobId <= 0)
            {
                error = $"Unable to start print job (Win32Error={Marshal.GetLastWin32Error()}).";
                return false;
            }

            try
            {
                if (!StartPagePrinter(hPrinter))
                {
                    error = $"Unable to start page (Win32Error={Marshal.GetLastWin32Error()}).";
                    return false;
                }

                try
                {
                    // Dot-matrix printers typically expect plain text + CRLF.
                    var payload = Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"));
                    if (!WritePrinter(hPrinter, payload, payload.Length, out var written) || written != payload.Length)
                    {
                        error = $"Unable to write to printer (Win32Error={Marshal.GetLastWin32Error()}).";
                        return false;
                    }
                }
                finally
                {
                    EndPagePrinter(hPrinter);
                }
            }
            finally
            {
                EndDocPrinter(hPrinter);
            }

            return true;
        }
        finally
        {
            ClosePrinter(hPrinter);
        }
    }
}
