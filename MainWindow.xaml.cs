using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SWF = System.Windows.Forms;

namespace HPD_getter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private ScreenState _last = ScreenState.Unknown;
        private string _lastSource = "";

        private class MonitorItem
        {
            public string DisplayName { get; set; }   // nice label for user
            public string DeviceName { get; set; }   // e.g. "\\.\DISPLAY1"

            public override string ToString() => DisplayName;
        }
        private MonitorItem _selectedMonitor;
        private bool _monitorPresent;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                Append("Logger started.");
                _timer.Interval = TimeSpan.FromSeconds(1);
                _timer.Tick += (_, ____) => CheckOnce();
                _timer.Start();
            };
        }
        private void CheckOnce()
        {
            // Try DDC/CI (authoritative if available)
            uint vcp;
            string monName;
            bool hasDdc = Ddc.TryReadPower(out vcp, out monName);
            if (hasDdc)
            {
                var now = (vcp == 1) ? ScreenState.On : ScreenState.Off; // 1=On, 4/5=Off/Standby
                if (now != _last || _lastSource != "DDC")
                {
                    Append(now == ScreenState.On ? "Screen turned ON (DDC)." : "Screen turned OFF (DDC).");
                    _last = now;
                    _lastSource = "DDC";
                }
                return;
            }

            // Fallback: HPD/driver presence (connected == ON, disconnected == OFF)
            bool present = Hpd.AnyPresent();
            var fb = present ? ScreenState.On : ScreenState.Off;
            if (fb != _last || _lastSource != "HPD")
            {
                Append(fb == ScreenState.On ? "Screen turned ON (HPD)." : "Screen turned OFF (HPD).");
                _last = fb;
                _lastSource = "HPD";
            }
        }

        private void RefreshMonitorList()
        {
            MonitorCombo.Items.Clear();

            foreach (var screen in SWF.Screen.AllScreens)
            {
                string label = $"{screen.DeviceName}  " +
                               $"{screen.Bounds.Width}x{screen.Bounds.Height} " +
                               (screen.Primary ? "(Primary)" : "");

                MonitorCombo.Items.Add(new MonitorItem
                {
                    DisplayName = label,
                    DeviceName = screen.DeviceName
                });
            }

            // Auto-guess external: first non-primary screen
            if (MonitorCombo.Items.Count > 0 && MonitorCombo.SelectedItem == null)
            {
                var screens = SWF.Screen.AllScreens;

                var nonPrimary = screens.FirstOrDefault(s => !s.Primary);
                if (nonPrimary != null)
                {
                    var item = MonitorCombo.Items
                        .Cast<MonitorItem>()
                        .First(i => i.DeviceName == nonPrimary.DeviceName);

                    MonitorCombo.SelectedItem = item;
                }
                else
                {
                    // Only one screen (probably laptop) → select it so UI isn’t empty
                    MonitorCombo.SelectedIndex = 0;
                }
            }

            _selectedMonitor = MonitorCombo.SelectedItem as MonitorItem;
        }

        private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedMonitor = MonitorCombo.SelectedItem as MonitorItem;
            _monitorPresent = false; // force a log on next check if state differs
            CheckSelectedMonitorPresence();
        }

        private void SystemEvents_DisplaySettingsChanged(object sender, EventArgs e)
        {
            // Display list may change (IDs reshuffled etc.), so refresh list
            RefreshMonitorList();
            CheckSelectedMonitorPresence();
        }

        private void CheckSelectedMonitorPresence()
        {
            if (_selectedMonitor == null)
                return;

            // Is there a screen with the same DeviceName currently active?
            bool presentNow = SWF.Screen
                .AllScreens
                .Any(s => s.DeviceName == _selectedMonitor.DeviceName);

            if (presentNow != _monitorPresent)
            {
                _monitorPresent = presentNow;
                LogMonitorState(presentNow);
            }
        }

        private void LogMonitorState(bool present)
        {
            string msg = present
                ? $"[{DateTime.Now:HH:mm:ss}] Monitor '{_selectedMonitor.DisplayName}' CONNECTED"
                : $"[{DateTime.Now:HH:mm:ss}] Monitor '{_selectedMonitor.DisplayName}' DISCONNECTED";

            LogBox.AppendText(msg + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            base.OnClosed(e);
        }

        private void Append(string line)
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }

    enum ScreenState { Unknown, On, Off }

    // --------- DDC/CI via Dxva2.dll (VCP 0xD6) ----------
    static class Ddc
    {
        public const byte VCP_POWER_MODE = 0xD6;

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("Dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

        [DllImport("Dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("Dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

        [DllImport("Dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, out uint type, out uint currentValue, out uint maxValue);

        // Reads the first reachable monitor's power mode (returns true if read succeeded)
        public static bool TryReadPower(out uint currentValue, out string monitorName)
        {
            // Initialize outs (required on all return paths)
            currentValue = 0;
            monitorName = null;

            // Locals that the callback will set
            bool gotOne = false;
            uint curLocal = 0;
            string nameLocal = null;

            MonitorEnumProc cb = delegate (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
            {
                if (gotOne) return true; // already found one, keep enumerating or early-out

                uint n;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out n) || n == 0)
                    return true;

                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr))
                    return true;

                try
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        uint type, cur, max;
                        if (GetVCPFeatureAndVCPFeatureReply(arr[i].hPhysicalMonitor, VCP_POWER_MODE, out type, out cur, out max))
                        {
                            // Store into locals, not the 'out' parameters
                            curLocal = cur;                               // 1=On, 4=Off, 5=Standby
                            nameLocal = arr[i].szPhysicalMonitorDescription;
                            gotOne = true;
                            break;
                        }
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(n, arr);
                }

                return true; // continue enumeration
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

            if (gotOne)
            {
                currentValue = curLocal;   // copy locals to outs AFTER the callback
                monitorName = nameLocal;
                return true;
            }

            return false;
        }

    }

    // --------- HPD presence via EnumDisplayDevices ----------
    static class Hpd
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        private const int DISPLAY_DEVICE_ACTIVE = 0x00000001;

        public static bool AnyPresent()
        {
            for (uint i = 0; ; i++)
            {
                var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;

                for (uint j = 0; ; j++)
                {
                    var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    if (!EnumDisplayDevices(adapter.DeviceName, j, ref mon, 0)) break;

                    if ((mon.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                        return true;
                }
            }
            return false;
        }
    }

}
