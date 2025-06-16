using LibreHardwareMonitor.Hardware;
using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;
using Computer = LibreHardwareMonitor.Hardware.Computer;
using Timer = System.Windows.Forms.Timer;


namespace TaskbarSystemMonitor
{
    public partial class MainForm : Form
    {
        private NotifyIcon notifyIcon;
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
            SetupTaskbarIcon();
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

        private void SetupTaskbarIcon()
        {
            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = CreateMonitorIcon();
            notifyIcon.Visible = true;
            notifyIcon.Text = "System Monitor";

            // Context menu
            ContextMenuStrip contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Exit", null, (s, e) => Application.Exit());
            notifyIcon.ContextMenuStrip = contextMenu;
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

                // Update taskbar icon
                notifyIcon.Icon = CreateMonitorIcon();
                notifyIcon.Text = $"CPU: {cpuUsage:F1}%\nRAM: {ramUsage:F1}%\nNet: {networkSpeed:F1} MB/s";
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

        private Icon CreateMonitorIcon()
        {
            const int size = 16;
            Bitmap bitmap = new Bitmap(size, size);

            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Draw CPU chart (red)
                DrawMiniChart(g, cpuHistory, Color.Red, new Rectangle(0, 0, 5, size));

                // Draw RAM chart (blue)
                DrawMiniChart(g, ramHistory, Color.Blue, new Rectangle(6, 0, 5, size));

                // Draw Network chart (green)
                DrawMiniChart(g, networkHistory, Color.Green, new Rectangle(12, 0, 4, size));
            }

            return Icon.FromHandle(bitmap.GetHicon());
        }

        private void DrawMiniChart(Graphics g, List<float> data, Color color, Rectangle bounds)
        {
            if (data.Count < 2) return;

            using (Pen pen = new Pen(color, 1))
            {
                float maxValue = Math.Max(data.Max(), 1); // Avoid division by zero

                for (int i = 1; i < data.Count; i++)
                {
                    float x1 = bounds.X + (float)(i - 1) * bounds.Width / (data.Count - 1);
                    float y1 = bounds.Bottom - (data[i - 1] / maxValue) * bounds.Height;
                    float x2 = bounds.X + (float)i * bounds.Width / (data.Count - 1);
                    float y2 = bounds.Bottom - (data[i] / maxValue) * bounds.Height;

                    g.DrawLine(pen, x1, y1, x2, y2);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                updateTimer?.Stop();
                updateTimer?.Dispose();
                computer?.Close();
                notifyIcon?.Dispose();
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