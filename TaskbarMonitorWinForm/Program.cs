
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;
using Timer = System.Windows.Forms.Timer;

namespace TaskbarSystemMonitor
{
    public partial class MainForm : Form
    {
        private NotifyIcon cpuNotifyIcon;
        private NotifyIcon ramNotifyIcon;
        private NotifyIcon networkNotifyIcon;
        private Computer computer;
        private Timer updateTimer;
        private List<float> cpuHistory = new List<float>();
        private List<float> ramHistory = new List<float>();
        private List<float> networkHistory = new List<float>();
        private long lastBytesReceived = 0;
        private DateTime lastNetworkCheck = DateTime.Now;
        private const int HISTORY_SIZE = 60; // Keep 60 data points

        public MainForm()
        {
            InitializeComponent();
            InitializeSystemMonitor();
            SetupTaskbarIcons();
            StartMonitoring();

            // Hide the form immediately
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visible = false;
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.AutoScaleDimensions = new SizeF(6F, 13F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(0, 0);
            this.FormBorderStyle = FormBorderStyle.None;
            this.Name = "MainForm";
            this.Text = "System Monitor";
            this.ResumeLayout(false);
        }

        private void InitializeSystemMonitor()
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true
            };
            computer.Open();
        }

        private void SetupTaskbarIcons()
        {
            // Setup CPU tray icon
            cpuNotifyIcon = new NotifyIcon();
            cpuNotifyIcon.Icon = CreateCpuIcon();
            cpuNotifyIcon.Visible = true;
            cpuNotifyIcon.Text = "CPU Monitor";
            cpuNotifyIcon.ContextMenuStrip = CreateContextMenu("CPU Monitor");

            // Setup RAM tray icon
            ramNotifyIcon = new NotifyIcon();
            ramNotifyIcon.Icon = CreateRamIcon();
            ramNotifyIcon.Visible = true;
            ramNotifyIcon.Text = "RAM Monitor";
            ramNotifyIcon.ContextMenuStrip = CreateContextMenu("RAM Monitor");

            // Setup Network tray icon
            networkNotifyIcon = new NotifyIcon();
            networkNotifyIcon.Icon = CreateNetworkIcon();
            networkNotifyIcon.Visible = true;
            networkNotifyIcon.Text = "Network Monitor";
            networkNotifyIcon.ContextMenuStrip = CreateContextMenu("Network Monitor");
        }

        private ContextMenuStrip CreateContextMenu(string title)
        {
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(title, null, null).Enabled = false; // Title item (disabled)
            contextMenu.Items.Add("-"); // Separator
            contextMenu.Items.Add("Exit All Monitors", null, (s, e) => Application.Exit());
            return contextMenu;
        }

        private void StartMonitoring()
        {
            updateTimer = new Timer();
            updateTimer.Interval = 1000; // Update every second
            updateTimer.Tick += UpdateSystemInfo;
            updateTimer.Start();
        }

        private void UpdateSystemInfo(object sender, EventArgs e)
        {
            try
            {
                computer.Accept(new UpdateVisitor());

                // Get CPU usage
                float cpuUsage = GetCpuUsage();
                cpuHistory.Add(cpuUsage);
                if (cpuHistory.Count > HISTORY_SIZE) cpuHistory.RemoveAt(0);

                // Get RAM usage
                float ramUsage = GetRamUsage();
                ramHistory.Add(ramUsage);
                if (ramHistory.Count > HISTORY_SIZE) ramHistory.RemoveAt(0);

                // Get Network speed
                float networkSpeed = GetNetworkSpeed();
                networkHistory.Add(networkSpeed);
                if (networkHistory.Count > HISTORY_SIZE) networkHistory.RemoveAt(0);

                // Update individual taskbar icons
                cpuNotifyIcon.Icon = CreateCpuIcon();
                cpuNotifyIcon.Text = $"CPU: {cpuUsage:F1}%";

                ramNotifyIcon.Icon = CreateRamIcon();
                ramNotifyIcon.Text = $"RAM: {ramUsage:F1}%";

                networkNotifyIcon.Icon = CreateNetworkIcon();
                networkNotifyIcon.Text = $"Network: {networkSpeed:F1} MB/s";
            }
            catch (Exception ex)
            {
                // Handle errors silently to keep the app running
                System.Diagnostics.Debug.WriteLine($"Error updating system info: {ex.Message}");
            }
        }

        private float GetCpuUsage()
        {
            var cpuSensors = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Cpu)
                .SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Load && s.Name == "CPU Total");

            return cpuSensors.FirstOrDefault()?.Value ?? 0;
        }

        private float GetRamUsage()
        {
            var memorySensors = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Memory)
                .SelectMany(h => h.Sensors)
                .Where(s => s.SensorType == SensorType.Load);

            return memorySensors.FirstOrDefault()?.Value ?? 0;
        }

        private float GetNetworkSpeed()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                               ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                long currentBytesReceived = interfaces.Sum(ni => ni.GetIPStatistics().BytesReceived);
                DateTime currentTime = DateTime.Now;

                if (lastBytesReceived > 0)
                {
                    double timeDiff = (currentTime - lastNetworkCheck).TotalSeconds;
                    double bytesDiff = currentBytesReceived - lastBytesReceived;
                    double mbps = (bytesDiff / timeDiff) / (1024 * 1024); // Convert to MB/s

                    lastBytesReceived = currentBytesReceived;
                    lastNetworkCheck = currentTime;

                    return (float)Math.Max(0, mbps);
                }

                lastBytesReceived = currentBytesReceived;
                lastNetworkCheck = currentTime;
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private Icon CreateCpuIcon()
        {
            return CreateSingleChart(cpuHistory, Color.Red, "CPU");
        }

        private Icon CreateRamIcon()
        {
            return CreateSingleChart(ramHistory, Color.Blue, "RAM");
        }

        private Icon CreateNetworkIcon()
        {
            return CreateSingleChart(networkHistory, Color.Green, "NET");
        }

        private Icon CreateSingleChart(List<float> data, Color color, string label)
        {
            const int size = 32;
            Bitmap bitmap = new Bitmap(size, size);

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Use most of the icon space for the chart
                Rectangle chartBounds = new Rectangle(2, 2, size - 4, size - 4);
                DrawFullSizeChart(g, data, color, chartBounds, label);
            }

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private void DrawFullSizeChart(Graphics g, List<float> data, Color color, Rectangle bounds, string label)
        {
            // Draw border
            using (Pen borderPen = new Pen(Color.Orange, 2))
            {
                g.DrawRectangle(borderPen, bounds);
            }

            // Fill background with semi-transparent black
            using (Brush bgBrush = new SolidBrush(Color.FromArgb(120, Color.Black)))
            {
                g.FillRectangle(bgBrush, bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
            }

            if (data.Count < 2)
            {
                // If no data, show label
                DrawLabel(g, label, bounds, color);
                return;
            }

            // Draw chart area (inset from border)
            Rectangle chartArea = new Rectangle(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6);

            using (Pen pen = new Pen(color, 1.5f))
            {
                // Draw the chart line
                for (int i = 1; i < Math.Min(data.Count, chartArea.Width); i++)
                {
                    int dataIndex1 = Math.Max(0, data.Count - chartArea.Width + i - 1);
                    int dataIndex2 = Math.Max(0, data.Count - chartArea.Width + i);

                    float x1 = chartArea.X + i - 1;
                    float y1 = chartArea.Bottom - (data[dataIndex1] / 100f) * chartArea.Height;
                    float x2 = chartArea.X + i;
                    float y2 = chartArea.Bottom - (data[dataIndex2] / 100f) * chartArea.Height;

                    // Ensure y coordinates are within bounds
                    y1 = Math.Max(chartArea.Y, Math.Min(chartArea.Bottom, y1));
                    y2 = Math.Max(chartArea.Y, Math.Min(chartArea.Bottom, y2));

                    g.DrawLine(pen, x1, y1, x2, y2);
                }

                // Draw current value indicator
                if (data.Count > 0)
                {
                    float currentValue = data.Last();
                    float x = chartArea.Right - 2;
                    float y = chartArea.Bottom - (currentValue / 100f) * chartArea.Height;
                    y = Math.Max(chartArea.Y, Math.Min(chartArea.Bottom, y));

                    using (Brush dotBrush = new SolidBrush(color))
                    {
                        g.FillEllipse(dotBrush, x - 2, y - 2, 4, 4);
                    }
                }
            }

            // Draw percentage text if there's data
            if (data.Count > 0)
            {
                string percentText = $"{data.Last():F0}%";
                using (Font font = new Font("Arial", 7, FontStyle.Bold))
                using (Brush textBrush = new SolidBrush(Color.White))
                {
                    SizeF textSize = g.MeasureString(percentText, font);
                    float textX = bounds.X + (bounds.Width - textSize.Width) / 2;
                    float textY = bounds.Bottom - textSize.Height - 2;

                    // Draw text shadow for better visibility
                    using (Brush shadowBrush = new SolidBrush(Color.Black))
                    {
                        g.DrawString(percentText, font, shadowBrush, textX + 1, textY + 1);
                    }
                    g.DrawString(percentText, font, textBrush, textX, textY);
                }
            }
        }

        private void DrawLabel(Graphics g, string label, Rectangle bounds, Color color)
        {
            using (Font font = new Font("Arial", 8, FontStyle.Bold))
            using (Brush textBrush = new SolidBrush(color))
            {
                SizeF textSize = g.MeasureString(label, font);
                float textX = bounds.X + (bounds.Width - textSize.Width) / 2;
                float textY = bounds.Y + (bounds.Height - textSize.Height) / 2;

                // Draw text shadow
                using (Brush shadowBrush = new SolidBrush(Color.Black))
                {
                    g.DrawString(label, font, shadowBrush, textX + 1, textY + 1);
                }
                g.DrawString(label, font, textBrush, textX, textY);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateTimer?.Stop();
                updateTimer?.Dispose();
                computer?.Close();
                cpuNotifyIcon?.Dispose();
                ramNotifyIcon?.Dispose();
                networkNotifyIcon?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false); // Never show the form
        }
    }

    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}