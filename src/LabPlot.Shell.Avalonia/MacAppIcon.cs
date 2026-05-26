using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

namespace LabPlot.Shell.Avalonia;

/// <summary>
/// `dotnet run` で起動した時 (= .app バンドルでない時) も macOS の Dock に LabPlot アイコンを
/// 出すための ObjC interop ヘルパ。配布用 .app バンドルでは Info.plist + .icns 経由で AppKit が
/// アイコンを認識するので不要だが、開発時は dotnet ホストの汎用アイコン (機関車) になってしまう。
///
/// 流れ: avares://LabPlot.Core.Avalonia/Assets/app-icon.png を読む → NSData → NSImage →
/// NSApp.setApplicationIconImage:image。NSImage / NSData はアプリ寿命の間保持されるので
/// retain count は気にしない (起動直後に 1 度だけ呼ぶ)。
///
/// macOS 以外では <see cref="TrySetDockIcon"/> を呼んでも DllImport の解決が走らないように、
/// <see cref="OperatingSystem.IsMacOS"/> ガードを入口で先にかける。
/// </summary>
internal static class MacAppIcon
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

    // objc_msgSend は引数の数と型で変則ディスパッチするので、必要なシグネチャを別エントリ名で
    // 用意する。CLR の P/Invoke は EntryPoint で同名 export を複数のメソッドにマップできる。
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr_BytesLength(IntPtr receiver, IntPtr selector, byte[] bytes, ulong length);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    /// <summary>
    /// PNG バイト列を NSImage に化けさせて NSApp.applicationIconImage に渡す。失敗しても例外を
    /// 上に出さない (起動経路で死んでもらいたくないため)。Windows / Linux では即 false を返す。
    /// </summary>
    public static bool TrySetDockIcon(Uri pngAssetUri)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            byte[] pngBytes;
            using (var stream = AssetLoader.Open(pngAssetUri))
            using (var memory = new MemoryStream())
            {
                stream.CopyTo(memory);
                pngBytes = memory.ToArray();
            }

            // NSData.dataWithBytes:length: → autoreleased NSData
            var nsDataClass = GetClass("NSData");
            var nsData = SendIntPtr_BytesLength(
                nsDataClass,
                GetSelector("dataWithBytes:length:"),
                pngBytes,
                (ulong)pngBytes.Length);
            if (nsData == IntPtr.Zero) return false;

            // NSImage alloc + initWithData:
            var nsImageClass = GetClass("NSImage");
            var nsImageAlloc = SendIntPtr(nsImageClass, GetSelector("alloc"));
            if (nsImageAlloc == IntPtr.Zero) return false;
            var nsImage = SendIntPtr_IntPtr(nsImageAlloc, GetSelector("initWithData:"), nsData);
            if (nsImage == IntPtr.Zero) return false;

            // NSApp = [NSApplication sharedApplication]
            var nsAppClass = GetClass("NSApplication");
            var nsApp = SendIntPtr(nsAppClass, GetSelector("sharedApplication"));
            if (nsApp == IntPtr.Zero) return false;

            // [NSApp setApplicationIconImage:image]
            SendVoid_IntPtr(nsApp, GetSelector("setApplicationIconImage:"), nsImage);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
