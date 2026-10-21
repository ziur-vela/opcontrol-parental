using System.Runtime.InteropServices;

namespace OpControl.Parental.Helper;

/// <summary>
/// A form existing is not the same as the child seeing it. Sample points across the window
/// and check that they belong to the form, so a fully covered or hidden window still gets
/// the normal message pop-up.
/// </summary>
internal static class WindowVisibility
{
    private const int GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
        public Point(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    public static bool IsOnScreen(Form form)
    {
        if (form.IsDisposed || !form.Visible) return false;
        var handle = form.Handle;
        if (!IsWindowVisible(handle) || IsIconic(handle)) return false;
        if (GetForegroundWindow() == handle) return true;

        if (!GetWindowRect(handle, out var rect)) return false;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        var hit = 0;
        var total = 0;
        for (var row = 1; row <= 5; row++)
        {
            for (var column = 1; column <= 5; column++)
            {
                total++;
                var x = rect.Left + width * column / 6;
                var y = rect.Top + height * row / 6;
                var target = GetAncestor(WindowFromPoint(new Point(x, y)), GaRoot);
                if (target == handle) hit++;
            }
        }
        return hit >= Math.Max(3, total / 3);
    }
}
