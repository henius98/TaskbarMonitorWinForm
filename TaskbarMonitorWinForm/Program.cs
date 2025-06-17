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

        // Chart drawing constants
        private const int ICON_SIZE = 32;
        private const int POINT_WIDTH = 2;
        private const int BORDER_WIDTH = 1;
        private readonly Color backgroundColor = Color.FromArgb(20, 20, 20);
        private readonly Color borderColor = Color.FromArgb(80, 80, 80);

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
            cpuNotifyIcon.Icon = CreateCpuIcon(0);
            cpuNotifyIcon.Visible = true;
            cpuNotifyIcon.Text = "CPU Monitor";
            cpuNotifyIcon.ContextMenuStrip = CreateContextMenu("CPU Monitor");

            // Setup RAM tray icon
            ramNotifyIcon = new NotifyIcon();
            ramNotifyIcon.Icon = CreateRamIcon(0, 0, 0);
            ramNotifyIcon.Visible = true;
            ramNotifyIcon.Text = "RAM Monitor";
            ramNotifyIcon.ContextMenuStrip = CreateContextMenu("RAM Monitor");

            // Setup Network tray icon
            networkNotifyIcon = new NotifyIcon();
            networkNotifyIcon.Icon = CreateNetworkIcon(0);
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

                // Get RAM usage
                GetRamUsage(out float ramUsage, out float ramAvailable, out float ramTotal);

                // Get Network speed
                float networkSpeed = GetNetworkSpeed();

                // Update individual taskbar icons with enhanced charts
                cpuNotifyIcon.Icon = CreateCpuIcon(cpuUsage);
                cpuNotifyIcon.Text = $"CPU: {cpuUsage:F1}%";

                ramNotifyIcon.Icon = CreateRamIcon(ramUsage, ramAvailable, ramTotal);
                ramNotifyIcon.Text = $"RAM: {((ramTotal - ramAvailable) / 1073741824):F1} / {(ramTotal / 1073741824):F1} GB ({ramUsage:F0}%)";

                networkNotifyIcon.Icon = CreateNetworkIcon(networkSpeed);
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

        private void GetRamUsage(out float ramUsage, out float ramAvailable, out float ramTotal)
        {
            var memoryHardware = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);

            if (memoryHardware != null)
            {
                var usedSensor = memoryHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used");
                var availableSensor = memoryHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Available");
                var loadSensor = memoryHardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "Memory");

                ramUsage = loadSensor?.Value ?? 0;
                ramAvailable = (availableSensor?.Value ?? 0) * 1024 * 1024 * 1024; // Convert GB to bytes
                ramTotal = ramAvailable + ((usedSensor?.Value ?? 0) * 1024 * 1024 * 1024); // Convert GB to bytes
            }
            else
            {
                // Fallback to performance counter or system info
                var totalMemory = GC.GetTotalMemory(false);
                ramUsage = 0;
                ramAvailable = totalMemory;
                ramTotal = totalMemory;
            }
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

        private Icon CreateCpuIcon(float cpuUsage)
        {
            cpuHistory.Add(cpuUsage);
            return CreateLineChart(cpuHistory, Color.Red, "CPU");
        }

        private Icon CreateRamIcon(float ramUsage, float ramAvailable, float ramTotal)
        {
            ramHistory.Add(ramUsage);
            return CreateLineChart(ramHistory, Color.Blue, "RAM");
        }

        private Icon CreateNetworkIcon(float networkSpeed)
        {
            // For network, we might want to normalize the scale differently
            // Let's assume max 100 MB/s for scaling purposes
            float normalizedSpeed = Math.Min(networkSpeed * 100f / 100f, 100f);
            networkHistory.Add(normalizedSpeed);
            return CreateLineChart(networkHistory, Color.Green, "NET");
        }

        private Icon CreateLineChart(List<float> measurements, Color foregroundColor, string label)
        {
            using (Bitmap bitmap = new Bitmap(ICON_SIZE, ICON_SIZE))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(backgroundColor);

                    // Limit measurements to fit the bitmap width
                    int maxPoints = bitmap.Width / POINT_WIDTH;
                    if (measurements.Count > maxPoints)
                    {
                        measurements.RemoveAt(0);
                    }

                    // Draw the line chart
                    if (measurements.Count > 0)
                    {
                        using (Pen linePen = new Pen(foregroundColor, POINT_WIDTH))
                        {
                            for (int i = measurements.Count - 1; i >= 0; i--)
                            {
                                float value = measurements[i];
                                var pos = bitmap.Width - (measurements.Count - 1 - i) * POINT_WIDTH;

                                // Calculate line height based on value (0-100%)
                                float lineHeight = bitmap.Height * value / 100f;

                                // Draw vertical line from bottom to the value height
                                graphics.DrawLine(linePen,
                                    pos, bitmap.Height - BORDER_WIDTH,
                                    pos, bitmap.Height - lineHeight - BORDER_WIDTH);
                            }
                        }
                    }

                    // Draw border
                    using (Pen borderPen = new Pen(borderColor, BORDER_WIDTH))
                    {
                        graphics.DrawRectangle(borderPen, 0, 0,
                            bitmap.Width - BORDER_WIDTH, bitmap.Height - BORDER_WIDTH);
                    }

                    // Draw current value text
                    if (measurements.Count > 0)
                    {
                        float currentValue = measurements.Last();
                        string valueText = $"{currentValue:F0}%";

                        using (Font font = new Font("Arial", 6, FontStyle.Bold))
                        using (Brush textBrush = new SolidBrush(Color.White))
                        {
                            SizeF textSize = graphics.MeasureString(valueText, font);
                            float textX = (bitmap.Width - textSize.Width) / 2;
                            float textY = bitmap.Height - textSize.Height - 2;

                            // Draw text shadow for better visibility
                            using (Brush shadowBrush = new SolidBrush(Color.Black))
                            {
                                graphics.DrawString(valueText, font, shadowBrush, textX + 1, textY + 1);
                            }
                            graphics.DrawString(valueText, font, textBrush, textX, textY);
                        }
                    }
                    else
                    {
                        // If no data, show label
                        DrawLabel(graphics, label, new Rectangle(0, 0, bitmap.Width, bitmap.Height), foregroundColor);
                    }

                    graphics.Save();
                }

                return Icon.FromHandle(bitmap.GetHicon());
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