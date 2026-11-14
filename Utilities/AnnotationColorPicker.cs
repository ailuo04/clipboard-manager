using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ClipboardManager.Utilities;

internal static class AnnotationColorPicker
{
    private static readonly int[] CustomColors = Enumerable.Repeat(0x00ffffff, 16).ToArray();

    public static Color? Pick(Window owner, Color initial)
    {
        IntPtr customColors = Marshal.AllocHGlobal(16 * sizeof(int));
        try
        {
            Marshal.Copy(CustomColors, 0, customColors, 16);
            var options = new ChooseColorOptions
            {
                Size = (uint)Marshal.SizeOf<ChooseColorOptions>(),
                Owner = new WindowInteropHelper(owner).Handle,
                Rgb = (uint)(initial.R | initial.G << 8 | initial.B << 16),
                CustomColors = customColors,
                Flags = 0x00000003 // CC_RGBINIT | CC_FULLOPEN
            };
            if (!ChooseColor(ref options)) return null;
            Marshal.Copy(customColors, CustomColors, 0, 16);
            return Color.FromRgb((byte)(options.Rgb & 0xff), (byte)((options.Rgb >> 8) & 0xff), (byte)((options.Rgb >> 16) & 0xff));
        }
        finally { Marshal.FreeHGlobal(customColors); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ChooseColorOptions
    {
        public uint Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public uint Rgb;
        public IntPtr CustomColors;
        public uint Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr Template;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorOptions options);
}
