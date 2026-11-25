using System.Runtime.InteropServices;

namespace FastGrep.Desktop;

internal static class MacDockIcon
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";

    public static void TryApply(string pngPath)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(pngPath))
        {
            return;
        }

        try
        {
            var utf8Path = Marshal.StringToCoTaskMemUTF8(pngPath);
            try
            {
                var nsString = Send(GetClass("NSString"), GetSelector("stringWithUTF8String:"), utf8Path);
                var image = Send(GetClass("NSImage"), GetSelector("alloc"));
                image = Send(image, GetSelector("initWithContentsOfFile:"), nsString);
                if (image == 0)
                {
                    return;
                }

                var application = Send(GetClass("NSApplication"), GetSelector("sharedApplication"));
                SendVoid(application, GetSelector("setApplicationIconImage:"), image);
                SendVoid(image, GetSelector("release"));
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8Path);
            }
        }
        catch
        {
            // The window-level icon remains active if a future macOS runtime blocks this API.
        }
    }

    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector(string name);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint argument);
}
