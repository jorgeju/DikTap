using System.Runtime.InteropServices;

namespace DikTap.Interop;

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig] int CompareElements(IntPtr el1, IntPtr el2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int CompareRuntimeIds(IntPtr r1, IntPtr r2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);
    [PreserveSig] int GetRootElement(out IntPtr root);
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);
    [PreserveSig] int ElementFromPoint(POINT pt, out IntPtr element);
    [PreserveSig] int GetFocusedElement(out IntPtr element);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
}
