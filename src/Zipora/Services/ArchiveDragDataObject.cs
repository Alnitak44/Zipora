using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Threading;

namespace Zipora.Services;

// OLE must see this interface on the same COM identity as IDataObject.
[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    void StartOperation(IntPtr reserved);
    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    void EndOperation(int result, IntPtr reserved, uint effects);
}

/// <summary>
/// Explorer gets names immediately and requests streams only when it needs file contents.
/// No extraction on selection or hover; no CF_HDROP paths pointing to incomplete files.
/// Shell's native IDataObject stores private drag image formats without WPF conversion.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ArchiveDragDataObject : IDataObject, IDataObjectAsyncCapability
{
    private readonly IDataObject _storage;
    private readonly ArchiveSelectionPlan _plan;
    private readonly Func<Task<string>> _prepare;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationToken _cancellation;
    private Task<string>? _prepared;
    private bool _asyncMode = true;
    public bool IsAsyncOperation { get; private set; }
    public bool ContentsRequested { get; private set; }
    public Exception? Failure { get; private set; }
    public Task<(int Result, uint Effect)> Completion => _completion.Task;
    private readonly TaskCompletionSource<(int, uint)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static readonly short DescriptorFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    public static readonly short ContentsFormat = unchecked((short)RegisterClipboardFormat("FileContents"));

    public ArchiveDragDataObject(ArchiveSelectionPlan plan, Func<Task<string>> prepare, CancellationToken cancellation = default)
    {
        _plan = plan;
        _cancellation = cancellation;
        _prepare = prepare;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var iid = typeof(IDataObject).GUID;
        Marshal.ThrowExceptionForHR(SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, ref iid, out _storage));
        PutBytes(DescriptorFormat, BuildDescriptors(plan));
        PutBytes(unchecked((short)RegisterClipboardFormat("Preferred DropEffect")), BitConverter.GetBytes(1));
    }

    internal static byte[] BuildDescriptors(ArchiveSelectionPlan plan)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.Unicode, true);
        writer.Write(plan.Items.Count);
        foreach (var item in plan.Items)
        {
            string name = item.ExportPath.Replace('/', '\\');
            if (name.Length >= 260) throw new PathTooLongException("所选路径过长，无法通过资源管理器拖放；请使用「解压到…」。");
            uint flags = 0x4 | 0x4000; // FD_ATTRIBUTES | FD_PROGRESSUI
            if (!item.IsDirectory && item.Size >= 0) flags |= 0x40;
            writer.Write(flags);
            writer.Write(new byte[32]); // CLSID, SIZEL, POINTL
            writer.Write(item.IsDirectory ? 0x10u : 0x80u);
            writer.Write(new byte[24]); // timestamps unspecified
            ulong size = (ulong)Math.Max(0, item.Size);
            writer.Write((uint)(size >> 32)); writer.Write((uint)size);
            byte[] chars = Encoding.Unicode.GetBytes(name);
            writer.Write(chars); writer.Write(new byte[520 - chars.Length]);
        }
        return buffer.ToArray();
    }

    private void PutBytes(short id, byte[] bytes)
    {
        IntPtr handle = GlobalAlloc(0x42, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null };
        try
        {
            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(handle); }
            var format = Format(id, TYMED.TYMED_HGLOBAL);
            _storage.SetData(ref format, ref medium, true);
            medium = default; // ownership transferred to Shell
        }
        finally { if (medium.unionmember != IntPtr.Zero) ReleaseStgMedium(ref medium); }
    }

    private string PreparedDirectory()
    {
        _cancellation.ThrowIfCancellationRequested();
        // Marshal extraction setup onto the UI STA. Only this STA touches the lazy task.
        if (!_dispatcher.CheckAccess()) return _dispatcher.Invoke(PreparedDirectory);
        _prepared ??= _prepare();
        if (!_prepared.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = _prepared.ContinueWith(_ => _dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            // Keep progress, cancel, repaint and COM calls alive during synchronous fallback.
            Dispatcher.PushFrame(frame);
        }
        return _prepared.GetAwaiter().GetResult();
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat != ContentsFormat) { _storage.GetData(ref format, out medium); return; }
        int hr = QueryGetData(ref format);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        if (format.lindex < 0) throw new COMException("Invalid stream index", unchecked((int)0x80040068));
        ContentsRequested = true;
        try
        {
            var item = _plan.Items[format.lindex];
            if (item.IsDirectory) throw new COMException("Directory has no stream", unchecked((int)0x80040068));
            string path = ArchiveSelectionPlan.ResolveInside(PreparedDirectory(), item.ArchivePath);
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, 0x20, 0, false, IntPtr.Zero, out var stream));
            try
            {
                medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM,
                    unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)), pUnkForRelease = null };
            }
            finally { Marshal.ReleaseComObject(stream); }
        }
        catch (Exception ex)
        {
            Failure = ex;
            throw new COMException(ex.Message, ex is OperationCanceledException ? unchecked((int)0x80004004) : unchecked((int)0x8003001E));
        }
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat != ContentsFormat) return _storage.QueryGetData(ref format);
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
        if ((format.tymed & TYMED.TYMED_ISTREAM) == 0) return unchecked((int)0x80040069);
        if (format.lindex < -1 || format.lindex >= _plan.Items.Count) return unchecked((int)0x80040068);
        return 0;
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) return _storage.EnumFormatEtc(direction);
        var formats = new List<FORMATETC>();
        var enumerator = _storage.EnumFormatEtc(direction);
        var next = new FORMATETC[1]; var fetched = new int[1];
        while (enumerator.Next(1, next, fetched) == 0) formats.Add(next[0]);
        formats.Add(Format(ContentsFormat, TYMED.TYMED_ISTREAM));
        return new FormatEnumerator(formats.ToArray());
    }
    internal static FORMATETC Format(short id, TYMED medium, int index = -1)
        => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium };
    public void GetDataHere(ref FORMATETC f, ref STGMEDIUM m) => throw new COMException("Not supported", unchecked((int)0x80004001));
    public int GetCanonicalFormatEtc(ref FORMATETC f, out FORMATETC result) { result = f; result.ptd = IntPtr.Zero; return 0x40130; }
    public void SetData(ref FORMATETC f, ref STGMEDIUM m, bool release) => _storage.SetData(ref f, ref m, release);
    public int DAdvise(ref FORMATETC f, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA? result) { result = null; return unchecked((int)0x80040003); }
    public void SetAsyncMode(bool value) => _asyncMode = value;
    public void GetAsyncMode(out bool value) => value = _asyncMode;
    public void StartOperation(IntPtr reserved) => IsAsyncOperation = true;
    public void InOperation(out bool value) => value = IsAsyncOperation;
    public void EndOperation(int result, IntPtr reserved, uint effects)
    { IsAsyncOperation = false; _completion.TrySetResult((result, effects)); }

    [DllImport("shell32.dll")] private static extern int SHCreateDataObject(IntPtr folder, uint count, IntPtr children, IntPtr inner, ref Guid iid, out IDataObject result);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IStream stream);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
{
    private int _position;
    public int Next(int count, FORMATETC[] output, int[]? fetched)
    {
        int copied = 0;
        while (copied < count && _position < formats.Length) output[copied++] = formats[_position++];
        if (fetched?.Length > 0) fetched[0] = copied;
        return copied == count ? 0 : 1;
    }
    public int Skip(int count) { int old = _position; _position = Math.Min(formats.Length, _position + count); return _position - old == count ? 0 : 1; }
    public int Reset() { _position = 0; return 0; }
    public void Clone(out IEnumFORMATETC copy) => copy = new FormatEnumerator(formats) { _position = _position };
}

[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDropSource
{
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keyState);
    [PreserveSig] int GiveFeedback(uint effect);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ArchiveDropSource(CancellationToken cancellation, Action<uint> feedback) : IOleDropSource
{
    public bool Released { get; private set; }
    public int QueryContinueDrag(bool escape, uint keyState)
    {
        if (escape || cancellation.IsCancellationRequested || (keyState & 2) != 0) return 0x40101;
        if ((keyState & 1) == 0) { Released = true; return 0x40100; }
        return 0;
    }
    public int GiveFeedback(uint effect) { feedback(effect); return 0x40102; }
    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int DoDragDrop(IDataObject data, IOleDropSource source, uint allowed, out uint effect);
}
