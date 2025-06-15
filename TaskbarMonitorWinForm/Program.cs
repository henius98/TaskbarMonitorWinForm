using System;
using System.Buffers;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TaskbarMonitor
{
    public sealed partial class Program : Form, IDisposable
    {
        // Three separate tray icons for independent charts
        private NotifyIcon? cpuTrayIcon;
        private NotifyIcon? ramTrayIcon;
        private NotifyIcon? networkTrayIcon;

        private System.Threading.Timer? updateTimer;
        private PerformanceCounter? cpuCounter;
        private PerformanceCounter? ramCounter;
        private PerformanceCounter? totalRamCounter;
        private NetworkInterface[]? networkInterfaces;
        private long lastBytesReceived = 0;
        private long lastBytesSent = 0;
        private long lastNetworkCheck = Environment.TickCount64;

        // Use circular buffers for better performance
        private const int HistorySize = 20;
        private readonly CircularBuffer<float> cpuHistory = new(HistorySize);
        private readonly CircularBuffer<float> ramHistory = new(HistorySize);
        private readonly CircularBuffer<float> networkHistory = new(HistorySize);

        private volatile float currentCpu = 0;
        private volatile float currentRam = 0;
        private volatile float currentNetwork = 0; // KB/s

        // Cache for performance - separate icons for each metric
        private Icon? lastCpuIcon;
        private Icon? lastRamIcon;
        private Icon? lastNetworkIcon;
        private readonly object iconLock = new();
        private bool disposed = false;

        public Program()
        {
            InitializeComponent();
            _ = InitializeMonitoringAsync();
            CreateTrayIcons();
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Visible = false;
        }

        private void InitializeComponent()
        {
            Text = "System Monitor";
            Size = new Size(1, 1);
            FormBorderStyle = FormBorderStyle.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.DoubleBuffer, true);
        }

        private async Task InitializeMonitoringAsync()
        {
            try
            {
                // Initialize performance counters on background thread
                await Task.Run(() =>
                {
                    cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    ramCounter = new PerformanceCounter("Memory", "Available MBytes");
                    totalRamCounter = new PerformanceCounter("Memory", "Committed Bytes");

                    networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(static ni => ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                    ni.OperationalStatus == OperationalStatus.Up)
                        .ToArray();

                    // First reading (CPU counter needs a baseline)
                    cpuCounter.NextValue();
                });

                // Use high-resolution timer
                updateTimer = new System.Threading.Timer(UpdateTimerCallback, null, 1000, 1000);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing performance counters: {ex.Message}");
            }
        }

        private void CreateTrayIcons()
        {
            // CPU Monitor Icon
            cpuTrayIcon = new NotifyIcon()
            {
                Icon = CreateIconWithText("CPU", Color.FromArgb(255, 60, 60)),
                Visible = true,
                Text = "CPU Monitor - Initializing..."
            };

            // RAM Monitor Icon
            ramTrayIcon = new NotifyIcon()
            {
                Icon = CreateIconWithText("RAM", Color.FromArgb(60, 120, 255)),
                Visible = true,
                Text = "RAM Monitor - Initializing..."
            };

            // Network Monitor Icon
            networkTrayIcon = new NotifyIcon()
            {
                Icon = CreateIconWithText("NET", Color.FromArgb(60, 255, 60)),
                Visible = true,
                Text = "Network Monitor - Initializing..."
            };

            // Context menus for each icon
            var exitMenuItem = new ToolStripMenuItem("Exit All Monitors", null, (s, e) =>
            {
                if (!disposed)
                {
                    Application.Exit();
                }
            });

            var cpuContextMenu = new ContextMenuStrip();
            cpuContextMenu.Items.Add("CPU Monitor", null).Enabled = false;
            cpuContextMenu.Items.Add(new ToolStripSeparator());
            cpuContextMenu.Items.Add(exitMenuItem);
            cpuTrayIcon.ContextMenuStrip = cpuContextMenu;

            var ramContextMenu = new ContextMenuStrip();
            ramContextMenu.Items.Add("RAM Monitor", null).Enabled = false;
            ramContextMenu.Items.Add(new ToolStripSeparator());
            ramContextMenu.Items.Add(exitMenuItem);
            ramTrayIcon.ContextMenuStrip = ramContextMenu;

            var networkContextMenu = new ContextMenuStrip();
            networkContextMenu.Items.Add("Network Monitor", null).Enabled = false;
            networkContextMenu.Items.Add(new ToolStripSeparator());
            networkContextMenu.Items.Add(exitMenuItem);
            networkTrayIcon.ContextMenuStrip = networkContextMenu;

            // Double click handlers
            cpuTrayIcon.DoubleClick += (s, e) => ToggleVisibility();
            ramTrayIcon.DoubleClick += (s, e) => ToggleVisibility();
            networkTrayIcon.DoubleClick += (s, e) => ToggleVisibility();
        }

        private void UpdateTimerCallback(object? state)
        {
            if (disposed) return;

            try
            {
                // Capture all metrics in parallel
                var tasks = new Task<(string type, float value)>[]
                {
                    Task.Run(() => ("CPU", GetCpuUsage())),
                    Task.Run(() => ("RAM", GetRamUsage())),
                    Task.Run(() => ("NET", GetNetworkUsage()))
                };

                Task.WaitAll(tasks, 800); // Timeout to prevent hanging

                foreach (var task in tasks)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var (type, value) = task.Result;
                        switch (type)
                        {
                            case "CPU":
                                currentCpu = value;
                                cpuHistory.Add(value);
                                break;
                            case "RAM":
                                currentRam = value;
                                ramHistory.Add(value);
                                break;
                            case "NET":
                                currentNetwork = value;
                                networkHistory.Add(value);
                                break;
                        }
                    }
                }

                // Update UI on main thread
                if (InvokeRequired)
                {
                    BeginInvoke(UpdateTrayIcons);
                }
                else
                {
                    UpdateTrayIcons();
                }
            }
            catch (Exception ex)
            {
                // Silently handle errors to keep running
                System.Diagnostics.Debug.WriteLine($"Update error: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetCpuUsage()
        {
            return cpuCounter?.NextValue() ?? 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetRamUsage()
        {
            if (ramCounter == null) return 0f;

            try
            {
                // Get available RAM in MB
                var availableRamMB = ramCounter.NextValue();

                // Get total physical memory using Win32 API for accuracy
                var totalRamMB = GetTotalPhysicalMemoryMB();

                if (totalRamMB <= 0 || availableRamMB < 0)
                    return 0f;

                // Calculate used RAM percentage
                var usedRamMB = Math.Max(0, totalRamMB - availableRamMB);
                var ramUsagePercent = (usedRamMB / totalRamMB) * 100f;

                // Clamp to valid range
                return Math.Clamp(ramUsagePercent, 0f, 100f);
            }
            catch
            {
                return 0f;
            }
        }

        private float GetNetworkUsage()
        {
            if (networkInterfaces == null) return 0f;

            try
            {
                long totalBytes = 0;

                // Use LINQ for better performance with modern .NET
                totalBytes = networkInterfaces
                    .AsParallel()
                    .Where(static ni => ni.OperationalStatus == OperationalStatus.Up)
                    .Sum(static ni =>
                    {
                        try
                        {
                            var stats = ni.GetIPv4Statistics();
                            return stats.BytesReceived + stats.BytesSent;
                        }
                        catch
                        {
                            return 0L;
                        }
                    });

                var currentTicks = Environment.TickCount64;
                var timeDiffMs = currentTicks - lastNetworkCheck;

                if (lastBytesReceived > 0 && timeDiffMs > 0)
                {
                    var bytesDiff = totalBytes - (lastBytesReceived + lastBytesSent);
                    var kbps = (float)(bytesDiff / (timeDiffMs / 1000.0) / 1024.0);

                    lastBytesReceived = totalBytes / 2; // Approximate split
                    lastBytesSent = totalBytes / 2;
                    lastNetworkCheck = currentTicks;

                    return Math.Max(0, kbps);
                }

                lastBytesReceived = totalBytes / 2;
                lastBytesSent = totalBytes / 2;
                lastNetworkCheck = currentTicks;
                return 0f;
            }
            catch
            {
                return 0f;
            }
        }

        private void UpdateTrayIcons()
        {
            if (disposed) return;

            // Update CPU Icon
            if (cpuTrayIcon != null)
            {
                cpuTrayIcon.Text = $"CPU: {currentCpu:F1}%";
                lock (iconLock)
                {
                    var newIcon = CreateCpuIcon();
                    var oldIcon = lastCpuIcon;
                    lastCpuIcon = newIcon;
                    cpuTrayIcon.Icon = newIcon;
                    oldIcon?.Dispose();
                }
            }

            // Update RAM Icon
            if (ramTrayIcon != null)
            {
                ramTrayIcon.Text = $"RAM: {currentRam:F1}%";
                lock (iconLock)
                {
                    var newIcon = CreateRamIcon();
                    var oldIcon = lastRamIcon;
                    lastRamIcon = newIcon;
                    ramTrayIcon.Icon = newIcon;
                    oldIcon?.Dispose();
                }
            }

            // Update Network Icon
            if (networkTrayIcon != null)
            {
                networkTrayIcon.Text = $"Network: {FormatNetworkSpeed(currentNetwork)}";
                lock (iconLock)
                {
                    var newIcon = CreateNetworkIcon();
                    var oldIcon = lastNetworkIcon;
                    lastNetworkIcon = newIcon;
                    networkTrayIcon.Icon = newIcon;
                    oldIcon?.Dispose();
                }
            }
        }

        private Icon CreateCpuIcon()
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Draw CPU chart
            DrawFullChart(g, cpuHistory.GetData(), new Rectangle(0, 0, 16, 16), Color.FromArgb(255, 60, 60));

            // Draw current value text
            using var font = new Font("Segoe UI", 6, FontStyle.Bold);
            var text = $"{currentCpu:F0}";
            var textColor = currentCpu > 80 ? Color.White : Color.FromArgb(200, 200, 200);
            using var brush = new SolidBrush(textColor);
            g.DrawString(text, font, brush, 1, 1);

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private Icon CreateRamIcon()
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Draw RAM chart
            DrawFullChart(g, ramHistory.GetData(), new Rectangle(0, 0, 16, 16), Color.FromArgb(60, 120, 255));

            // Draw current value text
            using var font = new Font("Segoe UI", 6, FontStyle.Bold);
            var text = $"{currentRam:F0}";
            var textColor = currentRam > 80 ? Color.White : Color.FromArgb(200, 200, 200);
            using var brush = new SolidBrush(textColor);
            g.DrawString(text, font, brush, 1, 1);

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private Icon CreateNetworkIcon()
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Normalize network data for chart (scale to 0-100 range)
            var networkData = networkHistory.GetData();
            var maxValue = networkData.Length > 0 ? Math.Max(networkData.ToArray().Max(), 1000f) : 1000f; // Min scale 1MB/s
            var normalizedData = new float[networkData.Length];
            for (int i = 0; i < networkData.Length; i++)
            {
                normalizedData[i] = (networkData[i] / maxValue) * 100f;
            }

            // Draw Network chart
            DrawFullChart(g, normalizedData, new Rectangle(0, 0, 16, 16), Color.FromArgb(60, 255, 60));

            // Draw current value text (show speed units)
            using var font = new Font("Segoe UI", 5, FontStyle.Bold);
            var text = currentNetwork < 1024 ? $"{currentNetwork:F0}K" : $"{currentNetwork / 1024:F1}M";
            var textColor = Color.FromArgb(200, 255, 200);
            using var brush = new SolidBrush(textColor);
            g.DrawString(text, font, brush, 1, 1);

            return Icon.FromHandle(bitmap.GetHicon());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawFullChart(Graphics g, ReadOnlySpan<float> data, Rectangle bounds, Color color)
        {
            if (data.Length < 2) return;

            // Rent array from pool for better performance
            var pointsArray = ArrayPool<PointF>.Shared.Rent(data.Length);
            try
            {
                var points = pointsArray.AsSpan(0, data.Length);

                for (int i = 0; i < data.Length; i++)
                {
                    float x = bounds.X + (float)i / (data.Length - 1) * bounds.Width;
                    float normalizedValue = Math.Clamp(data[i] / 100f, 0f, 1f);
                    float y = bounds.Bottom - normalizedValue * bounds.Height;
                    points[i] = new PointF(x, y);
                }

                // Draw background
                using var backgroundBrush = new SolidBrush(Color.FromArgb(30, 0, 0, 0));
                g.FillRectangle(backgroundBrush, bounds);

                // Draw chart line
                using var pen = new Pen(color, 1.5f);
                if (points.Length > 1)
                {
                    g.DrawLines(pen, points.ToArray());
                }

                // Fill area under curve for better visibility
                if (points.Length > 1)
                {
                    var fillPoints = new PointF[points.Length + 2];
                    points.ToArray().CopyTo(fillPoints, 0);
                    fillPoints[points.Length] = new PointF(bounds.Right, bounds.Bottom);
                    fillPoints[points.Length + 1] = new PointF(bounds.Left, bounds.Bottom);

                    using var fillBrush = new SolidBrush(Color.FromArgb(50, color.R, color.G, color.B));
                    g.FillPolygon(fillBrush, fillPoints);
                }
            }
            finally
            {
                ArrayPool<PointF>.Shared.Return(pointsArray);
            }
        }

        private static Icon CreateIconWithText(string text, Color color)
        {
            using var bitmap = new Bitmap(16, 16);
            using var g = Graphics.FromImage(bitmap);

            g.Clear(Color.FromArgb(40, 40, 40));
            using var font = new Font("Segoe UI", 5, FontStyle.Bold);
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, 1, 5);

            return Icon.FromHandle(bitmap.GetHicon());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetTotalPhysicalMemoryMB()
        {
            try
            {
                // Use WMI for accurate total memory detection
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var totalMemoryBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                    return totalMemoryBytes / (1024f * 1024f); // Convert to MB
                }
            }
            catch
            {
                // Fallback: estimate from GC and available memory
                try
                {
                    using var availableCounter = new PerformanceCounter("Memory", "Available Bytes");
                    var availableBytes = availableCounter.NextValue();
                    return (availableBytes + GC.GetTotalMemory(false)) / (1024f * 1024f);
                }
                catch
                {
                    return 8192f; // Default fallback (8GB)
                }
            }
            return 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string FormatNetworkSpeed(float kbps)
        {
            return kbps < 1024
                ? $"{kbps:F1} KB/s"
                : $"{kbps / 1024:F1} MB/s";
        }

        private void ToggleVisibility()
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                Show();
                WindowState = FormWindowState.Normal;
                BringToFront();
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        public new void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed && disposing)
            {
                disposed = true;

                updateTimer?.Dispose();
                cpuCounter?.Dispose();
                ramCounter?.Dispose();
                totalRamCounter?.Dispose();

                lock (iconLock)
                {
                    lastCpuIcon?.Dispose();
                    lastRamIcon?.Dispose();
                    lastNetworkIcon?.Dispose();
                }

                cpuTrayIcon?.Dispose();
                ramTrayIcon?.Dispose();
                networkTrayIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            // Ensure only one instance with better performance
            using var mutex = new Mutex(true, "TaskbarSystemMonitor_v2", out bool createdNew);
            if (createdNew)
            {
                // Set process priority for better responsiveness
                using var currentProcess = Process.GetCurrentProcess();
                currentProcess.PriorityClass = ProcessPriorityClass.High;

                Application.Run(new Program());
            }
            else
            {
                MessageBox.Show("System Monitor is already running.", "Taskbar Monitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    // High-performance circular buffer implementation
    public sealed class CircularBuffer<T> where T : struct
    {
        private readonly T[] buffer;
        private readonly int capacity;
        private int head = 0;
        private int count = 0;
        private readonly object lockObj = new();

        public CircularBuffer(int capacity)
        {
            this.capacity = capacity;
            buffer = new T[capacity];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            lock (lockObj)
            {
                buffer[head] = item;
                head = (head + 1) % capacity;
                if (count < capacity)
                    count++;
            }
        }

        public ReadOnlySpan<T> GetData()
        {
            lock (lockObj)
            {
                if (count == 0)
                    return ReadOnlySpan<T>.Empty;

                var result = new T[count];
                var start = count == capacity ? head : 0;

                for (int i = 0; i < count; i++)
                {
                    result[i] = buffer[(start + i) % capacity];
                }

                return result;
            }
        }
    }
}