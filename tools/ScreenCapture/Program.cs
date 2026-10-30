using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Forms;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        OhMyStudy screen capture: captures a window with Windows.Graphics.Capture, even when it is obscured or not focused.

        --executable <path>        Start this program and capture its main window
        --arguments "<args>"       Arguments for the started program
        --process <id>             Capture the main window of a running process
        --destination <png>        Required output PNG path
        --width <px> --height <px> Resize the window before capture (physical pixels)
        --delay-ms <ms>            Wait after the window appears; default 2000
        --settle-ms <ms>           Keep the newest frame for this long before saving; default 600
        --timeout-seconds <s>      Window wait; default 30
        --cursor                   Include the mouse cursor
        --close                    Close the started process afterwards
        """);
    return 0;
}

try { return Run(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static int Run(string[] args)
{
    var options = Options.Parse(args);
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    if (!GraphicsCaptureSession.IsSupported()) throw new PlatformNotSupportedException("Windows Graphics Capture is not supported on this system.");

    using var process = ResolveProcess(options);
    nint window = WaitForWindow(process, options.TimeoutSeconds);
    if (options.Width > 0) Resize(window, options.Width, options.Height);
    Thread.Sleep(TimeSpan.FromMilliseconds(options.DelayMilliseconds));

    try
    {
        using CaptureDevice device = CaptureDevice.Create();
        nint itemPointer = CreateItemForWindow(window);
        try
        {
            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            SizeInt32 poolSize = item.Size;
            if (poolSize.Width <= 0 || poolSize.Height <= 0) poolSize = new SizeInt32 { Width = 1920, Height = 1200 };
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device.Interop, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = options.Cursor;
            Direct3D11CaptureFrame? captured = null;
            using var arrived = new ManualResetEventSlim();
            pool.FrameArrived += (sender, _) =>
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                var previous = Interlocked.Exchange(ref captured, frame);
                previous?.Dispose();
                arrived.Set();
            };
            session.StartCapture();
            if (!arrived.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("No frame arrived from Windows Graphics Capture.");
            Thread.Sleep(Math.Max(0, options.SettleMilliseconds));
            using var frame = Interlocked.Exchange(ref captured, null) ?? throw new TimeoutException("The capture stream stopped before a frame could be read.");
            var size = frame.ContentSize;
            using var source = device.GetTexture(frame.Surface);
            using var staging = device.D3D.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None
            });
            device.Context.CopyResource(staging, source);
            var map = device.Context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try { SaveBitmap(map.DataPointer, (int)map.RowPitch, size.Width, size.Height, options.Destination); }
            finally { device.Context.Unmap(staging, 0); }
        }
        finally { Marshal.Release(itemPointer); }
    }
    finally { if (options.Close) Close(process); }

    Console.WriteLine(Path.GetFullPath(options.Destination));
    return 0;
}

static void SaveBitmap(nint data, int rowPitch, int width, int height, string destination)
{
    var pixels = new byte[width * height * 4];
    for (int row = 0; row < height; row++)
        Marshal.Copy(data + row * rowPitch, pixels, row * width * 4, width * 4);
    using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    bitmap.CopyFromBuffer(pixels.AsBuffer());
    string full = Path.GetFullPath(destination);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    using var file = File.Create(full);
    using var stream = file.AsRandomAccessStream();
    var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();
    encoder.SetSoftwareBitmap(bitmap);
    encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
}

static Process ResolveProcess(Options options)
{
    if (options.ProcessId is { } id) return Process.GetProcessById(id);
    if (options.Executable is null) throw new ArgumentException("Provide --process <id> or --executable <path>.");
    string executable = Path.GetFullPath(options.Executable);
    var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
    foreach (string argument in options.Arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
}

static nint WaitForWindow(Process process, int timeoutSeconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    do
    {
        process.Refresh();
        if (process.HasExited) throw new InvalidOperationException($"The process exited before opening a window. Exit code: {process.ExitCode}.");
        if (process.MainWindowHandle != 0) return process.MainWindowHandle;
        Thread.Sleep(100);
    } while (DateTime.UtcNow < deadline);
    throw new TimeoutException($"No window appeared within {timeoutSeconds} seconds.");
}

static void Resize(nint window, int width, int height)
{
    if (!Native.GetWindowRect(window, out var bounds)) throw new InvalidOperationException("GetWindowRect failed.");
    if (bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height) return;
    if (!Native.SetWindowPos(window, 0, bounds.Left, bounds.Top, width, height, Native.SwpNoZOrder | Native.SwpNoActivate))
        throw new InvalidOperationException("SetWindowPos failed.");
    Thread.Sleep(400);
}

static void Close(Process process)
{
    if (process.HasExited) return;
    process.CloseMainWindow();
    if (process.WaitForExit(10000)) return;
    process.Kill();
    process.WaitForExit();
}

static nint CreateItemForWindow(nint window)
{
    const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
    int hr = Native.WindowsCreateString(className, (uint)className.Length, out nint classId);
    if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
    nint factory = 0;
    try
    {
        Guid factoryId = typeof(IActivationFactory).GUID;
        hr = Native.RoGetActivationFactory(classId, ref factoryId, out factory);
    }
    finally { Native.WindowsDeleteString(classId); }
    if (hr < 0) throw new InvalidOperationException("The Windows Graphics Capture item factory is unavailable.", Marshal.GetExceptionForHR(hr));
    Guid interopId = typeof(IGraphicsCaptureItemInterop).GUID;
    hr = Marshal.QueryInterface(factory, in interopId, out nint interop);
    Marshal.Release(factory);
    if (hr < 0) throw new InvalidOperationException("Windows Graphics Capture interop is unavailable.", Marshal.GetExceptionForHR(hr));
    try
    {
        var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(GetVtableSlot(interop, 3));
        // GraphicsCaptureItem's ABI IID. The WinRT projection returns a type-hash GUID instead.
        Guid iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        hr = create(interop, window, ref iid, out nint value);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        return value;
    }
    finally { Marshal.Release(interop); }
}

static nint GetVtableSlot(nint instance, int index)
{
    nint vtable = Marshal.ReadIntPtr(instance);
    return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int CreateForWindowDelegate(nint factory, nint window, ref Guid iid, out nint value);

[ComImport]
[Guid("00000035-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivationFactory
{
}

internal sealed class CaptureDevice : IDisposable
{
    public required ID3D11Device D3D { get; init; }
    public required ID3D11DeviceContext Context { get; init; }
    public required IDirect3DDevice Interop { get; init; }

    public static CaptureDevice Create()
    {
        var result = D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_0], out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
        if (result.Failure || device is null || context is null)
            throw new InvalidOperationException("Direct3D 11 device creation failed.", Marshal.GetExceptionForHR(result.Code));
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        int hr = Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint inspectable);
        if (hr < 0) { device.Dispose(); context.Dispose(); throw Marshal.GetExceptionForHR(hr)!; }
        return new() { D3D = device, Context = context, Interop = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable) };
    }

    public ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        try
        {
            Guid iid = typeof(ID3D11Texture2D).GUID;
            int hr = access.GetInterface(ref iid, out nint pointer);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            return new ID3D11Texture2D(pointer);
        }
        finally { Marshal.ReleaseComObject(access); }
    }

    public void Dispose()
    {
        Interop.Dispose();
        Context.Dispose();
        D3D.Dispose();
    }
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    [PreserveSig] int GetInterface(ref Guid iid, out nint value);
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig] int CreateForWindow(nint window, ref Guid iid, out nint value);
    [PreserveSig] int CreateForMonitor(nint monitor, ref Guid iid, out nint value);
}

internal sealed record Options(string? Executable, int? ProcessId, string Destination, int Width, int Height,
    int DelayMilliseconds, int TimeoutSeconds, bool Cursor, bool Close, string[] Arguments, int SettleMilliseconds)
{
    public static Options Parse(string[] args)
    {
        string? executable = null, destination = null, arguments = null;
        int? processId = null;
        int width = 0, height = 0, delay = 2000, timeout = 30, settle = 600;
        bool cursor = false, close = false;
        for (int index = 0; index < args.Length; index++)
        {
            string name = args[index];
            string Next() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"Missing value for {name}.");
            switch (name)
            {
                case "--executable": executable = Next(); break;
                case "--process": processId = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--destination": destination = Next(); break;
                case "--width": width = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--height": height = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--delay-ms": delay = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--settle-ms": settle = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--timeout-seconds": timeout = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--arguments": arguments = Next(); break;
                case "--cursor": cursor = true; break;
                case "--close": close = true; break;
                default: throw new ArgumentException($"Unknown argument: {name}");
            }
        }
        if (destination is null) throw new ArgumentException("Provide --destination <png>.");
        return new(executable, processId, destination, width, height, delay, timeout, cursor, close,
            arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [], settle);
    }
}

internal static partial class Native
{
    public const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out Rect rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("d3d11.dll")]
    public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint inspectable);

    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WindowsCreateString(string source, uint length, out nint value);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint value);
}
