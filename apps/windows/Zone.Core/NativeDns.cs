using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Zone.Core;

// Layouts match windns.h. Every async request and callback is rooted until the native
// completion, including after a caller stops waiting. Never free an in-flight cancel handle.
internal static class NativeDns
{
  internal const int Pending = 9506;
  internal const int Cancelled = 1223;
  [UnmanagedFunctionPointer(CallingConvention.Winapi)]
  internal delegate void Callback(int status, IntPtr context, IntPtr result);

  [StructLayout(LayoutKind.Sequential)]
  internal struct Query
  {
    public uint Version, InterfaceIndex;
    public IntPtr Name, Callback, Context;
  }
  [StructLayout(LayoutKind.Sequential)]
  internal struct Registration
  {
    public uint Version, InterfaceIndex;
    public IntPtr Instance, Callback, Context, Credentials;
    public int Unicast;
  }
  [StructLayout(LayoutKind.Sequential)]
  internal struct Instance
  {
    public IntPtr Name, Host, IPv4, IPv6;
    public ushort Port, Priority, Weight;
    public uint PropertyCount;
    public IntPtr Keys, Values;
    public uint InterfaceIndex;
  }
  [StructLayout(LayoutKind.Sequential)]
  internal struct Record
  {
    public IntPtr Next, Name;
    public ushort Type, DataLength;
    public uint Flags, Ttl, Reserved;
    public IntPtr Target; // First member of the PTR union; only read for Type == 12.
  }

  [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
  internal static extern IntPtr DnsServiceConstructInstance(string service, string host, IntPtr ipv4, IntPtr ipv6,
    ushort port, ushort priority, ushort weight, uint count,
    [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
    [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern void DnsServiceFreeInstance(IntPtr instance);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern void DnsRecordListFree(IntPtr records, int freeType);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern int DnsServiceRegister(IntPtr request, IntPtr cancel);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern int DnsServiceDeRegister(IntPtr request, IntPtr cancel);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern int DnsServiceBrowse(IntPtr request, IntPtr cancel);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern int DnsServiceBrowseCancel(IntPtr cancel);
  [DllImport("dnsapi.dll", ExactSpelling = true)]
  internal static extern int DnsServiceResolve(IntPtr request, IntPtr cancel);

  internal static IntPtr Allocate<T>(T value) where T : struct
  {
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
    Marshal.StructureToPtr(value, pointer, false);
    return pointer;
  }

  internal static Exception Error(int status) => new CoreException($"Windows DNS-SD: {new Win32Exception(status).Message} ({status}).");

  // Single-completion register/resolve operations. Result memory belongs to the callback.
  internal sealed class Operation<T>
  {
    readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Func<IntPtr, T> decode;
    readonly Callback callback;
    readonly List<IntPtr> allocations = [];
    GCHandle root;
    int finished;
    public Task<T> Task => completion.Task;

    public Operation(Func<IntPtr, T> decode)
    {
      this.decode = decode;
      callback = Complete;
      root = GCHandle.Alloc(this);
    }

    public IntPtr Keep(IntPtr pointer) { allocations.Add(pointer); return pointer; }
    public IntPtr CallbackPointer => Marshal.GetFunctionPointerForDelegate(callback);
    public void Started(int status) { if (status != Pending) Complete(status, IntPtr.Zero, IntPtr.Zero); }

    void Complete(int status, IntPtr context, IntPtr result)
    {
      if (Interlocked.Exchange(ref finished, 1) != 0) return;
      try
      {
        if (status != 0) completion.TrySetException(Error(status));
        else completion.TrySetResult(decode(result));
      }
      catch (Exception error) { completion.TrySetException(error); }
      finally
      {
        if (result != IntPtr.Zero) DnsServiceFreeInstance(result);
        foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        root.Free();
      }
    }
  }
}
