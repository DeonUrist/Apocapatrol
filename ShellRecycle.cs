using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Apocapatrol
{
    // Native IFileOperation avoids managed COM wrappers (Unity/Mono). RECYCLEONDELETE never falls back to File.Delete.
    internal static class ShellRecycle
    {
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Flags(IntPtr self, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Delete(IntPtr self, IntPtr item, IntPtr sink);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Perform(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Aborted(IntPtr self, out int aborted);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Release(IntPtr self);
        private static T Method<T>(IntPtr instance, int slot) where T : class
        {
            IntPtr method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(method, typeof(T));
        }
        private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        internal static void Move(string path)
        {
            string file = Path.GetFullPath(path); Exception failure = null;
            var thread = new Thread(() =>
            {
                IntPtr operation = IntPtr.Zero, item = IntPtr.Zero; bool initialized = false;
                try
                {
                    Check(CoInitializeEx(IntPtr.Zero, 2)); initialized = true;
                    Guid clsid = new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), iid = new Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
                    Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out operation));
                    Check(Method<Flags>(operation, 5)(operation, 0x00080000 | 0x00100000 | 0x0040 | 0x0010 | 0x0004 | 0x0400));
                    iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); Check(SHCreateItemFromParsingName(file, IntPtr.Zero, ref iid, out item));
                    Check(Method<Delete>(operation, 18)(operation, item, IntPtr.Zero)); Check(Method<Perform>(operation, 21)(operation));
                    int aborted; Check(Method<Aborted>(operation, 22)(operation, out aborted));
                    if (aborted != 0 || File.Exists(file)) throw new IOException("Windows did not recycle the template");
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    if (item != IntPtr.Zero) Method<Release>(item, 2)(item);
                    if (operation != IntPtr.Zero) Method<Release>(operation, 2)(operation);
                    if (initialized) CoUninitialize();
                }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) throw new IOException("Template could not be moved to Windows Recycle Bin", failure);
        }
    }
}
