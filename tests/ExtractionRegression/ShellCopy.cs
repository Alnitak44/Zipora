using System.Runtime.InteropServices;
using System.IO;
using Zipora.Services;

internal static class ShellCopy
{
    // Tests the OS copy engine consuming virtual-file IDataObject, without UI automation.
    public static void Copy(ArchiveDragDataObject data, string destination, bool asynchronous = false)
    {
        var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination, IntPtr.Zero, ref iid, out var folder));
        var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
        var dropId = new Guid("00000122-0000-0000-C000-000000000046");
        folder.BindToHandler(IntPtr.Zero, ref handler, ref dropId, out var pointer);
        var target = (IDropTarget)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);
        try
        {
            data.SetAsyncMode(asynchronous);
            uint effect = 1;
            target.DragEnter(data, 1, 0, ref effect);
            if (effect != 1) throw new IOException("Shell rejected copy effect.");
            target.Drop(data, 0, 0, ref effect);
            if (effect != 1) throw new IOException("Shell did not copy.");
        }
        finally { Marshal.ReleaseComObject(target); Marshal.ReleaseComObject(folder); }
    }
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        void DragEnter(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, long point, ref uint effect);
        void DragOver(uint keys, long point, ref uint effect);
        void DragLeave();
        void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, uint keys, long point, ref uint effect);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid,
        out IShellItem item);
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bind, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hints, out int order);
    }
}
