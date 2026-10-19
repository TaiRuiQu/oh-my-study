using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    // RenderTargetBitmap omits ElementCompositionPreview visuals. Capture the owned,
    // foreground client area after composition settles, including real formula surfaces.
    private unsafe (byte[] Pixels, int Width, int Height) CaptureNativeClient()
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeCaptureGetClientRect(window, out var rect)) throw new Win32Exception();
        var origin = new CapturePoint();
        if (!NativeCaptureClientToScreen(window, ref origin)) throw new Win32Exception();
        int width = rect.Right, height = rect.Bottom;
        nint screen = NativeCaptureGetDC(0), memory = 0, bitmap = 0, previous = 0;
        try
        {
            if (screen == 0) throw new Win32Exception();
            memory = NativeCaptureCreateCompatibleDC(screen);
            var info = new CaptureBitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = NativeCaptureCreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) throw new Win32Exception();
            previous = NativeCaptureSelectObject(memory, bitmap);
            if (!NativeCaptureBitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x40CC0020)) throw new Win32Exception();
            var pixels = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            return (pixels, width, height);
        }
        finally
        {
            if (previous != 0) NativeCaptureSelectObject(memory, previous);
            if (bitmap != 0) NativeCaptureDeleteObject(bitmap);
            if (memory != 0) NativeCaptureDeleteDC(memory);
            if (screen != 0) NativeCaptureReleaseDC(0, screen);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeCaptureSetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)] private struct CaptureRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CapturePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureBitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
        internal uint Compression, SizeImage; internal int XPels, YPels; internal uint Used, Important;
    }
    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureGetClientRect(nint window, out CaptureRect rect);
    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureClientToScreen(nint window, ref CapturePoint point);
    [LibraryImport("user32.dll", EntryPoint = "GetDC")] private static partial nint NativeCaptureGetDC(nint window);
    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")] private static partial int NativeCaptureReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static partial nint NativeCaptureCreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureDeleteDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureDeleteObject(nint value);
    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")] private static partial nint NativeCaptureSelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")] private static partial nint NativeCaptureCreateDIBSection(nint dc, ref CaptureBitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll", EntryPoint = "BitBlt", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureBitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
