using System.Runtime.InteropServices;

namespace Parakeet.Engine.LlamaServer;

/// <summary>What the Vulkan loader says this machine's graphics is, for the one decision that
/// turns on it: where a mixture-of-experts model's expert weights are placed.</summary>
public enum GpuClass
{
    /// <summary>
    /// Nothing could be enumerated — no loader, no device, or a call that failed. Not a synonym
    /// for "integrated": it means the question was not answered, and the caller decides what an
    /// unanswered question is worth. <see cref="LlamaServerProcess.BuildEnvironment"/> treats it
    /// as integrated, because the two failures are not symmetric — see there.
    /// </summary>
    Unknown = 0,

    /// <summary>Every device enumerated reports VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU.</summary>
    Integrated = 1,

    /// <summary>At least one device reports VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU.</summary>
    Discrete = 2,
}

/// <summary>
/// What the loader reported: which kind of graphics, and how much memory the device itself has.
/// </summary>
/// <param name="Class">The device type, per <see cref="GpuClass"/>.</param>
/// <param name="DeviceLocalBytes">
/// The largest VK_MEMORY_HEAP_DEVICE_LOCAL heap on the device that answered
/// <see cref="GpuClass.Discrete"/>, and <b>zero for every other answer</b> — deliberately, because
/// on a UMA device the device-local heap is system memory and the figure would answer a different
/// question than the one its name suggests. Largest rather than the sum: a card reports one such
/// heap, and adding them would double-count on a driver that splits it.
/// </param>
public sealed record VulkanGraphics(GpuClass Class, long DeviceLocalBytes)
{
    /// <summary>The answer when the loader could not be asked at all.</summary>
    public static VulkanGraphics Unknown { get; } = new(GpuClass.Unknown, 0);
}

/// <summary>
/// Asks the Vulkan loader whether this machine's graphics is a card of its own or part of the
/// processor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the loader and not WMI or DXGI.</b> <c>VkPhysicalDeviceProperties.deviceType</c> is the
/// enum the question is actually about, reported by the same API the backend this decides for
/// will run on. The alternatives infer it: DXGI from a dedicated-memory figure that an integrated
/// adapter reports as a small non-zero number, WMI from an <c>AdapterRAM</c> field capped at 4 GB
/// and wrong on every card above it. An inference that is usually right is the wrong shape for a
/// setting whose failure is silent slowness.
/// </para>
/// <para>
/// <b>Any discrete device present answers Discrete.</b> A laptop with both reports two devices,
/// and llama.cpp's default main-gpu takes the first the backend enumerates — which on such a
/// machine is the card, not the processor's half. The rule is therefore "is there a card here",
/// which is the question the placement decision is really asking; a machine where ggml picks the
/// integrated device anyway is one where the Settings picker exists to say so.
/// <b>Unmeasured</b> — no machine here has both, and docs/UNPROVEN.md says so.
/// </para>
/// <para>
/// <b>Every failure is Unknown, never an exception.</b> This runs on the path that starts a
/// model, and a missing Vulkan loader is the normal state of a machine that will run the CPU
/// drop. The interop is hand-written against <c>vulkan_core.h</c> the way
/// <c>Parakeet.App.Services.Mpv.MpvNative</c> is written against <c>client.h</c>: the constants
/// and the offset below are ABI, not style, and must not be "tidied".
/// </para>
/// </remarks>
public static partial class VulkanDeviceProbe
{
    private const string LibraryName = "vulkan-1";

    /// <summary>VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO.</summary>
    private const int InstanceCreateInfoType = 1;

    /// <summary>VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU.</summary>
    private const int IntegratedGpu = 1;

    /// <summary>VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU.</summary>
    private const int DiscreteGpu = 2;

    /// <summary>VK_SUCCESS. VK_INCOMPLETE (5) is also a usable answer where noted.</summary>
    private const int Success = 0;

    /// <summary>
    /// sizeof(VkInstanceCreateInfo) on x64: sType, 4 bytes of padding, pNext, flags, 4 more,
    /// then four pointers and two counts. Every field but sType is zero here — no layers, no
    /// extensions, no application info, all of which are optional.
    /// </summary>
    private const int InstanceCreateInfoBytes = 64;

    /// <summary>
    /// Byte offset of <c>deviceType</c> in VkPhysicalDeviceProperties: four uint32 before it.
    /// </summary>
    private const int DeviceTypeOffset = 16;

    /// <summary>
    /// Room for the whole of VkPhysicalDeviceProperties, which is 824 bytes on x64 — the call
    /// writes all of it, so a buffer sized to the one field that is read would be a stack
    /// overwrite. Generous rather than exact: the struct grows only with a new core version, and
    /// the cost of the margin is nothing.
    /// </summary>
    private const int PropertiesBytes = 2048;

    /// <summary>
    /// Room for the whole of VkPhysicalDeviceMemoryProperties, 520 bytes on x64, on the same
    /// grounds as <see cref="PropertiesBytes"/>.
    /// </summary>
    private const int MemoryPropertiesBytes = 1024;

    /// <summary>
    /// Byte offsets inside VkPhysicalDeviceMemoryProperties: a uint32 type count, then 32
    /// VkMemoryType of 8 bytes each, then the heap count, then the heaps — which are 8-aligned
    /// because a VkDeviceSize leads each one. ABI, copied from vulkan_core.h by hand.
    /// </summary>
    private const int MemoryHeapCountOffset = 4 + (32 * 8);

    private const int MemoryHeapsOffset = MemoryHeapCountOffset + 4;

    /// <summary>VkMemoryHeap: a VkDeviceSize, its flags, and four bytes of tail padding.</summary>
    private const int MemoryHeapStride = 16;

    private const int MemoryHeapFlagsOffset = 8;

    /// <summary>VK_MAX_MEMORY_HEAPS.</summary>
    private const int MaxMemoryHeaps = 16;

    /// <summary>VK_MEMORY_HEAP_DEVICE_LOCAL_BIT.</summary>
    private const int DeviceLocalHeap = 1;

    private static readonly Lazy<VulkanGraphics> Cached = new(Probe, isThreadSafe: true);

    /// <summary>
    /// What this machine has, enumerated once per process. Cached because the answer cannot
    /// change while the application runs and creating an instance loads every installed driver.
    /// </summary>
    public static VulkanGraphics Describe() => Cached.Value;

    /// <summary>The uncached probe, for the one test that is allowed to call the loader.</summary>
    internal static VulkanGraphics Probe()
    {
        var createInfo = Marshal.AllocHGlobal(InstanceCreateInfoBytes);
        var properties = Marshal.AllocHGlobal(PropertiesBytes);
        var memory = Marshal.AllocHGlobal(MemoryPropertiesBytes);
        nint instance = 0;

        try
        {
            for (var offset = 0; offset < InstanceCreateInfoBytes; offset += 4)
            {
                Marshal.WriteInt32(createInfo, offset, 0);
            }

            Marshal.WriteInt32(createInfo, 0, InstanceCreateInfoType);

            if (vkCreateInstance(createInfo, 0, out instance) != Success || instance == 0)
            {
                return VulkanGraphics.Unknown;
            }

            uint count = 0;

            // Null for the array asks for the count only. VK_INCOMPLETE cannot happen on this
            // call, so anything but VK_SUCCESS is a failure to answer.
            if (vkEnumeratePhysicalDevices(instance, ref count, 0) != Success || count == 0)
            {
                return VulkanGraphics.Unknown;
            }

            var handles = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            try
            {
                if (vkEnumeratePhysicalDevices(instance, ref count, handles) != Success)
                {
                    return VulkanGraphics.Unknown;
                }

                var answer = VulkanGraphics.Unknown;
                for (var index = 0; index < count; index++)
                {
                    var device = Marshal.ReadIntPtr(handles, index * IntPtr.Size);
                    vkGetPhysicalDeviceProperties(device, properties);

                    switch (Marshal.ReadInt32(properties, DeviceTypeOffset))
                    {
                        // A card ends the search: see the remarks on why any is enough. Its
                        // memory is read here and nowhere else, because it is the only answer
                        // the figure means anything for.
                        case DiscreteGpu:
                            vkGetPhysicalDeviceMemoryProperties(device, memory);
                            return new VulkanGraphics(GpuClass.Discrete, LargestDeviceLocalHeap(memory));

                        // Kept rather than returned, so a later device can still be a card. No
                        // memory figure: on a UMA device the device-local heap is system memory.
                        case IntegratedGpu:
                            answer = new VulkanGraphics(GpuClass.Integrated, 0);
                            break;

                        // VIRTUAL_GPU, CPU and OTHER answer neither question. A software device
                        // is not a card, and it is not the processor's graphics either.
                        default:
                            break;
                    }
                }

                return answer;
            }
            finally
            {
                Marshal.FreeHGlobal(handles);
            }
        }
        catch (DllNotFoundException)
        {
            // The normal state of a machine running the CPU drop, and of every Linux CI runner.
            return VulkanGraphics.Unknown;
        }
        catch (EntryPointNotFoundException)
        {
            return VulkanGraphics.Unknown;
        }
        catch (BadImageFormatException)
        {
            return VulkanGraphics.Unknown;
        }
        finally
        {
            if (instance != 0)
            {
                vkDestroyInstance(instance, 0);
            }

            Marshal.FreeHGlobal(memory);
            Marshal.FreeHGlobal(properties);
            Marshal.FreeHGlobal(createInfo);
        }
    }

    /// <summary>
    /// The largest device-local heap in a filled VkPhysicalDeviceMemoryProperties, or zero when
    /// there is none — which is not a state a card should report, and is therefore treated by the
    /// caller exactly as "could not be asked".
    /// </summary>
    private static long LargestDeviceLocalHeap(nint memory)
    {
        var heaps = Math.Min(Marshal.ReadInt32(memory, MemoryHeapCountOffset), MaxMemoryHeaps);
        var largest = 0L;

        for (var index = 0; index < heaps; index++)
        {
            var heap = MemoryHeapsOffset + (index * MemoryHeapStride);
            if ((Marshal.ReadInt32(memory, heap + MemoryHeapFlagsOffset) & DeviceLocalHeap) == 0)
            {
                continue;
            }

            largest = Math.Max(largest, Marshal.ReadInt64(memory, heap));
        }

        return largest;
    }

    [LibraryImport(LibraryName)]
    private static partial int vkCreateInstance(nint createInfo, nint allocator, out nint instance);

    [LibraryImport(LibraryName)]
    private static partial int vkEnumeratePhysicalDevices(nint instance, ref uint count, nint devices);

    [LibraryImport(LibraryName)]
    private static partial void vkGetPhysicalDeviceProperties(nint device, nint properties);

    [LibraryImport(LibraryName)]
    private static partial void vkGetPhysicalDeviceMemoryProperties(nint device, nint properties);

    [LibraryImport(LibraryName)]
    private static partial void vkDestroyInstance(nint instance, nint allocator);
}
