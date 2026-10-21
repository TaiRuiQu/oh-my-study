using System.ComponentModel;
using System.Runtime.InteropServices;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    // A screen-region BitBlt used to be the only way to see composed WinUI content, but it
    // also copied whatever window happened to overlap the app and required the app to be in
    // front. PrintWindow with PW_RENDERFULLCONTENT asks the compositor for this window's own
    // surface instead, so overlap is irrelevant and composition surfaces (formulas,
    // acrylic) stay visible while the app remains in the background. The full window is
    // printed and the client area is cropped out, because the output contract is the
    // client-sized bitmap.
    //
    // WinUI 3 hosts flyouts, menus and ComboBox drop-downs in separate top-level popup
    // windows owned by the main window. Those are not part of the main window's surface, so
    // a snapshot of the main HWND alone would silently drop open menus. (ContentDialog
    // content stays in the main window surface.) After the main capture we walk the
    // z-order, keep the visible popup windows owned by the main window, capture each one by
    // HWND and composite it over the main pixels at its position relative to the client
    // origin, bottom-up so the topmost popup wins. Popup transparent margins come back from
    // PrintWindow as pure black; a border flood fill marks exactly those margin pixels and
    // leaves the already captured main-window pixels there, so no screen read is needed.
    private unsafe (byte[] Pixels, int Width, int Height) CaptureNativeClient()
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (!NativeCaptureGetClientRect(window, out var client)) throw new Win32Exception();
        var origin = new CapturePoint();
        if (!NativeCaptureClientToScreen(window, ref origin)) throw new Win32Exception();
        int width = client.Right, height = client.Bottom;
        if (width <= 0 || height <= 0) throw new Win32Exception();
        if (!NativeCaptureGetWindowRect(window, out var bounds)) throw new Win32Exception();
        int frameWidth = bounds.Right - bounds.Left, frameHeight = bounds.Bottom - bounds.Top;
        if (frameWidth <= 0 || frameHeight <= 0) throw new Win32Exception();
        // A blank snapshot must not be replaced by unrelated screen content: background
        // capture cannot fall back to the screen, so report the failure instead.
        if (!TryPrintWindow(window, frameWidth, frameHeight, out var frame) || LooksBlank(frame))
            throw new Win32Exception("PrintWindow could not capture the application window.");
        var pixels = new byte[checked(width * height * 4)];
        CropClientArea(frame, frameWidth, frameHeight, pixels, width, height,
            origin.X - bounds.Left, origin.Y - bounds.Top);
        CompositeOwnedPopups(window, pixels, width, height, origin);
        for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        return (pixels, width, height);
    }

    private static void CropClientArea(byte[] frame, int frameWidth, int frameHeight, byte[] pixels,
        int width, int height, int offsetX, int offsetY)
    {
        int sourceX = Math.Max(0, offsetX);
        int targetX = Math.Max(0, -offsetX);
        int copyWidth = Math.Min(width - targetX, frameWidth - sourceX);
        if (copyWidth <= 0) return;
        for (int y = 0; y < height; y++)
        {
            int sourceY = y + offsetY;
            if ((uint)sourceY >= (uint)frameHeight) continue;
            Buffer.BlockCopy(frame, ((sourceY * frameWidth) + sourceX) * 4, pixels, ((y * width) + targetX) * 4, copyWidth * 4);
        }
    }

    private static bool LooksBlank(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
            if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0) return false;
        return true;
    }

    private static void CompositeOwnedPopups(nint window, byte[] pixels, int width, int height, CapturePoint origin)
    {
        var popups = new List<(nint Window, CaptureRect Bounds)>();
        for (nint candidate = NativeCaptureGetWindow(window, CaptureWindowFirst);
            candidate != 0;
            candidate = NativeCaptureGetWindow(candidate, CaptureWindowNext))
        {
            if (candidate == window) continue;
            if (NativeCaptureGetWindow(candidate, CaptureWindowOwner) != window) continue;
            if (!NativeCaptureIsWindowVisible(candidate) || NativeCaptureIsIconic(candidate)) continue;
            if (!NativeCaptureGetWindowRect(candidate, out var bounds)) continue;
            if (bounds.Right - bounds.Left < 2 || bounds.Bottom - bounds.Top < 2) continue;
            popups.Add((candidate, bounds));
        }
        // The enumeration walks the z-order from top to bottom; paint in reverse so the
        // popup that the user sees on top ends up on top of the capture as well.
        for (int index = popups.Count - 1; index >= 0; index--)
        {
            var (popup, bounds) = popups[index];
            int popupWidth = bounds.Right - bounds.Left, popupHeight = bounds.Bottom - bounds.Top;
            if (!TryPrintWindow(popup, popupWidth, popupHeight, out var content) || LooksBlank(content))
            {
                AppLog.Failure("NativeCapture", $"Popup window 0x{popup:X} did not provide printable content; it is missing from this capture.");
                continue;
            }
            var margin = FindPopupMargin(content, popupWidth, popupHeight);
            CompositePopup(content, margin, popupWidth, popupHeight, pixels,
                width, height, bounds.Left - origin.X, bounds.Top - origin.Y);
        }
    }

    // Popup window rectangles include the transparent margin that holds the drop shadow.
    // PrintWindow renders that margin pure black, while the visible content starts a few
    // pixels inside. Only black pixels reachable from the window border count as margin:
    // black text, icons or backgrounds inside the popup are not connected to the border and
    // stay content. The flood is limited to a margin band so a legitimately black popup
    // background cannot be swallowed by the fill.
    private static bool[] FindPopupMargin(byte[] content, int width, int height)
    {
        var margin = new bool[width * height];
        var pending = new Stack<int>();
        void Seed(int x, int y)
        {
            int index = (y * width) + x;
            if (margin[index] || !IsMarginPixel(content, index, x, y, width, height)) return;
            margin[index] = true;
            pending.Push(index);
        }
        for (int x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
        for (int y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }
        while (pending.Count > 0)
        {
            int index = pending.Pop();
            int x = index % width, y = index / width;
            if (x > 0) Seed(x - 1, y);
            if (x + 1 < width) Seed(x + 1, y);
            if (y > 0) Seed(x, y - 1);
            if (y + 1 < height) Seed(x, y + 1);
        }
        return margin;
    }

    private static bool IsMarginPixel(byte[] content, int index, int x, int y, int width, int height)
    {
        const int marginLimit = 32, darkness = 8;
        if (Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y)) >= marginLimit) return false;
        int pixel = index * 4;
        return content[pixel] <= darkness && content[pixel + 1] <= darkness && content[pixel + 2] <= darkness;
    }

    // Margin pixels keep what the main-window capture already has at that client position;
    // every other popup pixel wins. No screen pixels are involved, so the composite is safe
    // while another window covers the app. The rounded corners are anti-aliased against the
    // transparent margin in the printed surface, so the one or two pixel fringe next to the
    // margin takes the colour of the content just inside it instead of a dark edge colour.
    private static void CompositePopup(byte[] content, bool[] margin, int popupWidth, int popupHeight,
        byte[] pixels, int width, int height, int left, int top)
    {
        var distance = PopupMarginDistance(margin, popupWidth, popupHeight);
        for (int y = 0; y < popupHeight; y++)
        {
            int targetY = top + y;
            if ((uint)targetY >= (uint)height) continue;
            for (int x = 0; x < popupWidth; x++)
            {
                int index = (y * popupWidth) + x;
                if (margin[index]) continue;
                int targetX = left + x;
                if ((uint)targetX >= (uint)width) continue;
                int source = index;
                if (distance[index] <= 2 && InnerPopupPixel(distance, popupWidth, popupHeight, x, y) is int inner) source = inner;
                int sourcePixel = source * 4, target = ((targetY * width) + targetX) * 4;
                pixels[target] = content[sourcePixel];
                pixels[target + 1] = content[sourcePixel + 1];
                pixels[target + 2] = content[sourcePixel + 2];
            }
        }
    }

    private static int[] PopupMarginDistance(bool[] margin, int width, int height)
    {
        var distance = new int[width * height];
        var pending = new Queue<int>();
        for (int index = 0; index < distance.Length; index++)
        {
            distance[index] = margin[index] ? 0 : int.MaxValue;
            if (margin[index]) pending.Enqueue(index);
        }
        while (pending.Count > 0)
        {
            int index = pending.Dequeue();
            if (distance[index] >= 3) continue;
            int next = distance[index] + 1;
            int x = index % width, y = index / width;
            if (x > 0) Visit(index - 1, next);
            if (x + 1 < width) Visit(index + 1, next);
            if (y > 0) Visit(index - width, next);
            if (y + 1 < height) Visit(index + width, next);
        }
        return distance;

        void Visit(int neighbour, int value)
        {
            if (distance[neighbour] <= value) return;
            distance[neighbour] = value;
            pending.Enqueue(neighbour);
        }
    }

    private static int? InnerPopupPixel(int[] distance, int width, int height, int x, int y)
    {
        int best = -1, bestDistance = distance[(y * width) + x];
        for (int offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0) continue;
                int neighbourX = x + offsetX, neighbourY = y + offsetY;
                if ((uint)neighbourX >= (uint)width || (uint)neighbourY >= (uint)height) continue;
                int neighbour = (neighbourY * width) + neighbourX;
                if (distance[neighbour] <= bestDistance) continue;
                bestDistance = distance[neighbour];
                best = neighbour;
            }
        }
        return best < 0 ? null : best;
    }

    private static unsafe bool TryPrintWindow(nint window, int width, int height, out byte[] pixels)
    {
        pixels = new byte[checked(width * height * 4)];
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = NativeCaptureGetDC(0);
            if (screen == 0) return false;
            memory = NativeCaptureCreateCompatibleDC(screen);
            var info = new CaptureBitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = NativeCaptureCreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
            if (memory == 0 || bitmap == 0 || bits == 0) return false;
            previous = NativeCaptureSelectObject(memory, bitmap);
            if (!NativeCapturePrintWindow(window, memory, CaptureRenderFullContent)) return false;
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return true;
        }
        finally
        {
            if (previous != 0) NativeCaptureSelectObject(memory, previous);
            if (bitmap != 0) NativeCaptureDeleteObject(bitmap);
            if (memory != 0) NativeCaptureDeleteDC(memory);
            if (screen != 0) NativeCaptureReleaseDC(0, screen);
        }
    }

    private const uint CaptureRenderFullContent = 0x00000002;
    private const uint CaptureWindowFirst = 0;
    private const uint CaptureWindowNext = 2;
    private const uint CaptureWindowOwner = 4;

    [StructLayout(LayoutKind.Sequential)] private struct CaptureRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CapturePoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureBitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
        internal uint Compression, SizeImage; internal int XPels, YPels; internal uint Used, Important;
    }
    [LibraryImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureGetClientRect(nint window, out CaptureRect rect);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureGetWindowRect(nint window, out CaptureRect rect);
    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureClientToScreen(nint window, ref CapturePoint point);
    [LibraryImport("user32.dll", EntryPoint = "GetWindow")] private static partial nint NativeCaptureGetWindow(nint window, uint command);
    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureIsWindowVisible(nint window);
    [LibraryImport("user32.dll", EntryPoint = "IsIconic")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureIsIconic(nint window);
    [LibraryImport("user32.dll", EntryPoint = "PrintWindow", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCapturePrintWindow(nint window, nint dc, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetDC")] private static partial nint NativeCaptureGetDC(nint window);
    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")] private static partial int NativeCaptureReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static partial nint NativeCaptureCreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureDeleteDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool NativeCaptureDeleteObject(nint value);
    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")] private static partial nint NativeCaptureSelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection")] private static partial nint NativeCaptureCreateDIBSection(nint dc, ref CaptureBitmapInfo info, uint usage, out nint bits, nint section, uint offset);
}
