using System.Runtime.InteropServices;
using System.Text;

namespace OhMyStudy.Documents;

public sealed record RestorationGpu(int Index, string Name, ulong DedicatedMemoryBytes, bool Integrated, bool RuntimeUsable);
public sealed record RestorationGpuStatus(RestorationGpu? Device, string Reason);

public static partial class DocumentRestorationGpu
{
    public const ulong MinimumExclusiveMemoryBytes = 12UL * 1024 * 1024 * 1024;

    public static RestorationGpuStatus Select(IEnumerable<RestorationGpu> devices)
    {
        var candidates = devices.Where(d => !d.Integrated && d.DedicatedMemoryBytes > MinimumExclusiveMemoryBytes).ToArray();
        var selected = candidates.Where(d => d.RuntimeUsable).OrderByDescending(d => d.DedicatedMemoryBytes).ThenBy(d => d.Index).FirstOrDefault();
        return selected is not null ? new(selected, "Available")
            : candidates.Length > 0 ? new(null, "Install a compatible NVIDIA CUDA runtime and driver to enable DocRes.")
            : new(null, "DocRes requires one discrete NVIDIA GPU with more than 12 GiB of dedicated memory. Exactly 12 GiB is not sufficient.");
    }

    public static RestorationGpuStatus Probe(string? additionalRuntimeDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) return new(null, "DocRes CUDA probing requires Windows.");
        try
        {
            if (CuInit(0) != 0 || CuDeviceGetCount(out int count) != 0) return new(null, "The NVIDIA driver could not initialize CUDA. Install or update the NVIDIA driver.");
            nint runtime = LoadRuntime(additionalRuntimeDirectory);
            try
            {
                var devices = new List<RestorationGpu>();
                for (int ordinal = 0; ordinal < count; ordinal++)
                {
                    if (CuDeviceGet(out int device, ordinal) != 0 || CuDeviceTotalMem(out nuint memory, device) != 0 || CuDeviceGetAttribute(out int integrated, 18, device) != 0) continue;
                    byte[] name = new byte[256], bus = new byte[32];
                    CuDeviceGetName(name, name.Length, device);
                    bool usable = runtime != 0 && CuDeviceGetPciBusId(bus, bus.Length, device) == 0 && RuntimeMatches(runtime, Decode(bus));
                    devices.Add(new(ordinal, Decode(name), memory, integrated != 0, usable));
                }
                return Select(devices);
            }
            finally { if (runtime != 0) NativeLibrary.Free(runtime); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(null, "The NVIDIA CUDA driver or installed runtime is unavailable: " + error.Message);
        }
    }

    private static nint LoadRuntime(string? additionalDirectory)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(additionalDirectory)) directories.Add(additionalDirectory);
        string? cuda = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrWhiteSpace(cuda)) directories.Add(Path.Combine(cuda, "bin"));
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA GPU Computing Toolkit", "CUDA");
        if (Directory.Exists(root)) directories.AddRange(Directory.EnumerateDirectories(root).OrderDescending().Select(path => Path.Combine(path, "bin")));
        foreach (string directory in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string path in Directory.EnumerateFiles(directory, "cudart64_*.dll").OrderDescending())
                if (NativeLibrary.TryLoad(path, out nint runtime)) return runtime;
        return 0;
    }

    private static bool RuntimeMatches(nint runtime, string driverBus)
    {
        var count = Marshal.GetDelegateForFunctionPointer<GetDeviceCount>(NativeLibrary.GetExport(runtime, "cudaGetDeviceCount"));
        var bus = Marshal.GetDelegateForFunctionPointer<GetBusId>(NativeLibrary.GetExport(runtime, "cudaDeviceGetPCIBusId"));
        var select = Marshal.GetDelegateForFunctionPointer<SetDevice>(NativeLibrary.GetExport(runtime, "cudaSetDevice"));
        var initialize = Marshal.GetDelegateForFunctionPointer<FreeMemory>(NativeLibrary.GetExport(runtime, "cudaFree"));
        if (count(out int devices) != 0) return false;
        for (int index = 0; index < devices; index++)
        {
            byte[] address = new byte[32];
            if (bus(address, address.Length, index) == 0 && Decode(address).Equals(driverBus, StringComparison.OrdinalIgnoreCase))
                return select(index) == 0 && initialize(0) == 0;
        }
        return false;
    }

    private static string Decode(byte[] value) => Encoding.UTF8.GetString(value.AsSpan(0, Array.IndexOf(value, (byte)0) is int end && end >= 0 ? end : value.Length));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceCount(out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBusId([Out] byte[] id, int length, int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetDevice(int device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FreeMemory(nint memory);

    [LibraryImport("nvcuda.dll", EntryPoint = "cuInit")]
    private static partial int CuInit(uint flags);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetCount")]
    private static partial int CuDeviceGetCount(out int count);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGet")]
    private static partial int CuDeviceGet(out int device, int ordinal);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceTotalMem_v2")]
    private static partial int CuDeviceTotalMem(out nuint bytes, int device);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetAttribute")]
    private static partial int CuDeviceGetAttribute(out int value, int attribute, int device);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetName")]
    private static partial int CuDeviceGetName([Out] byte[] name, int length, int device);
    [LibraryImport("nvcuda.dll", EntryPoint = "cuDeviceGetPCIBusId")]
    private static partial int CuDeviceGetPciBusId([Out] byte[] id, int length, int device);
}
