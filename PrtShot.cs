using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("PrtShot")]
[assembly: System.Reflection.AssemblyProduct("PrtShot")]
[assembly: System.Reflection.AssemblyVersion("1.2.3.0")]

namespace PrtShot
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var instance = new Mutex(true, @"Local\PrtShot", out created))
            {
                if (!created)
                    return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                using (var tray = new TrayApplication(icon))
                    Application.Run(tray);
            }
        }
    }

    internal sealed class TrayApplication : ApplicationContext
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "PrtShot";

        private readonly NotifyIcon icon;
        private readonly ContextMenuStrip menu;
        private readonly HotkeyWindow hotkey;
        private readonly ToolStripMenuItem hotkeyItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly System.Windows.Forms.Timer selectionTimer;
        private SelectionForm selection;
        private Bitmap selectionImage;
        private IntPtr capturedForeground;
        private IntPtr pendingForeground;

        public TrayApplication(Icon appIcon)
        {
            hotkey = new HotkeyWindow(delegate { Capture(true); });
            selectionTimer = new System.Windows.Forms.Timer { Interval = 150 };
            selectionTimer.Tick += delegate { UpdateSelection(GetForegroundWindow()); };
            menu = new ContextMenuStrip();
            menu.Items.Add("Выделить область", null, delegate { Capture(false); });
            menu.Items.Add("Открыть папку снимков", null, delegate { OpenFolder(); });
            menu.Items.Add(new ToolStripSeparator());
            hotkeyItem = new ToolStripMenuItem();
            hotkeyItem.Click += delegate { RegisterPrintScreen(); };
            menu.Items.Add(hotkeyItem);
            startupItem = new ToolStripMenuItem("Запускать вместе с Windows");
            startupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitThread(); });
            menu.Opening += delegate { startupItem.Checked = IsInStartup(); };

            icon = new NotifyIcon { Icon = appIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
            icon.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                    Capture(false);
            };
            RegisterPrintScreen();
        }

        private void RegisterPrintScreen()
        {
            hotkey.TryRegister();
            bool available = hotkey.PrintScreenAvailable;
            hotkeyItem.Text = available ? "Print Screen" : "Повторить регистрацию Print Screen";
            hotkeyItem.Enabled = !available;
            icon.Text = available ? "PrtShot — Print Screen" : "PrtShot — Print Screen недоступен";
        }

        private void Capture(bool watchForeground)
        {
            IntPtr foreground = watchForeground ? GetForegroundWindow() : IntPtr.Zero;
            try
            {
                if (selection != null)
                {
                    if (selection.Visible && (capturedForeground == foreground || foreground == IntPtr.Zero))
                    {
                        selection.RaiseWithoutActivation();
                        return;
                    }

                    CloseSelection();
                }

                capturedForeground = foreground;
                Rectangle desktop = SystemInformation.VirtualScreen;
                selectionImage = DesktopCapture.Take(desktop);
                selection = new SelectionForm(selectionImage, desktop);
                selection.FormClosed += SaveSelection;
                selection.Show();
                hotkey.SetSelectionKeys(selection.HandleSelectionKey);
                if (watchForeground)
                    selectionTimer.Start();
            }
            catch (Exception error)
            {
                if (selection != null)
                {
                    selection.FormClosed -= SaveSelection;
                    selection.Dispose();
                }
                ResetSelection();
                ShowCaptureError(error.Message);
            }
        }

        private void UpdateSelection(IntPtr foreground)
        {
            if (selection == null)
                return;

            if (foreground == IntPtr.Zero)
            {
                pendingForeground = IntPtr.Zero;
                return;
            }

            if (capturedForeground == IntPtr.Zero)
                capturedForeground = foreground;

            if (foreground != capturedForeground)
            {
                if (foreground == pendingForeground)
                    CloseSelection();
                else
                    pendingForeground = foreground;
                return;
            }

            pendingForeground = IntPtr.Zero;
            selection.EnsureAbove(foreground);
        }

        private void CloseSelection()
        {
            var closing = selection;
            closing.Close();
            if (selection == closing)
            {
                // Closing a form without a window handle does not raise FormClosed.
                closing.Dispose();
                ResetSelection();
            }
        }

        private void SaveSelection(object sender, FormClosedEventArgs e)
        {
            var closed = (SelectionForm)sender;
            try
            {
                if (closed.DialogResult != DialogResult.OK)
                    return;

                Rectangle imageBounds = new Rectangle(Point.Empty, selectionImage.Size);
                Rectangle area = Rectangle.Intersect(closed.Area, imageBounds);
                if (area.Width <= 0 || area.Height <= 0)
                {
                    ShowCaptureError("Выделенная область пуста.");
                    return;
                }

                using (Bitmap crop = selectionImage.Clone(area, PixelFormat.Format24bppRgb))
                {
                    string saved = ScreenshotFiles.Save(crop, ScreenshotFiles.Folder);
                    if (!closed.CopyRequested)
                        return;

                    try
                    {
                        var data = new DataObject();
                        data.SetImage(crop);
                        Clipboard.SetDataObject(data, true, 8, 40);
                    }
                    catch (ExternalException)
                    {
                        ShowCaptureError("Буфер обмена занят. Снимок сохранён: " + saved);
                    }
                }
            }
            catch (Exception error)
            {
                ShowCaptureError(error.Message);
            }
            finally
            {
                ResetSelection();
            }
        }

        private void ResetSelection()
        {
            selectionTimer.Stop();
            capturedForeground = IntPtr.Zero;
            pendingForeground = IntPtr.Zero;
            hotkey.SetSelectionKeys(null);
            selection = null;
            if (selectionImage != null)
            {
                selectionImage.Dispose();
                selectionImage = null;
            }
        }

        private void ShowCaptureError(string text)
        {
            icon.ShowBalloonTip(4000, "PrtShot", text, ToolTipIcon.Warning);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(ScreenshotFiles.Folder);
                Process.Start(new ProcessStartInfo(ScreenshotFiles.Folder) { UseShellExecute = true });
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private static bool IsInStartup()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }

        private static void ToggleStartup()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key.GetValue(RunName) == null)
                        key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(RunName, false);
                }
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(message, "PrtShot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (selection != null)
                    CloseSelection();
                selectionTimer.Dispose();
                icon.Visible = false;
                icon.Dispose();
                menu.Dispose();
                hotkey.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int PrintScreenId = 1;
        private const int CancelId = 4;
        private const int SaveId = 5;
        private const int CopyId = 6;
        private const int HotkeyMessage = 0x0312;
        private const int RawInputMessage = 0x00FF;
        private const int CaptureFallbackMessage = 0x8001;
        private const uint NoRepeat = 0x4000;
        private const uint InputSink = 0x00000100;
        private const uint RemoveDevice = 0x00000001;
        private const uint ReadInput = 0x10000003;
        private const uint KeyboardType = 1;
        private const ushort Released = 1;
        private const ushort PrintScreen = 0x2C;
        private const uint SamePressWindowMs = 100;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
        private static readonly uint InputSize = (uint)Marshal.SizeOf(typeof(RawKeyboardInput));
        private static readonly uint DeviceSize = (uint)Marshal.SizeOf(typeof(RawInputDevice));

        private readonly Action capture;
        private Action<Keys> selectionKey;
        private bool printScreenRegistered;
        private bool rawInputRegistered;
        private bool printScreenDown;
        private bool plainPress;
        private bool pressHandled;
        private bool fallbackQueued;
        private int? lastPrintScreenHotkey;

        public bool PrintScreenAvailable { get { return printScreenRegistered || rawInputRegistered; } }

        public HotkeyWindow(Action capture)
        {
            this.capture = capture;
            CreateHandle(new CreateParams { Caption = "PrtShotHotkey", Parent = new IntPtr(-3) });
        }

        public void TryRegister()
        {
            if (!printScreenRegistered)
                printScreenRegistered = RegisterHotKey(Handle, PrintScreenId, NoRepeat, PrintScreen);
            if (!rawInputRegistered)
                rawInputRegistered = RegisterKeyboard(InputSink, Handle);
        }

        public void SetSelectionKeys(Action<Keys> handler)
        {
            if (selectionKey != null)
            {
                UnregisterHotKey(Handle, CancelId);
                UnregisterHotKey(Handle, SaveId);
                UnregisterHotKey(Handle, CopyId);
            }

            selectionKey = handler;
            if (handler != null)
            {
                RegisterHotKey(Handle, CancelId, NoRepeat, (uint)Keys.Escape);
                RegisterHotKey(Handle, SaveId, NoRepeat, (uint)Keys.Enter);
                RegisterHotKey(Handle, CopyId, NoRepeat | 0x0002, (uint)Keys.C);
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage)
            {
                int hotkeyId = message.WParam.ToInt32();
                if (hotkeyId == PrintScreenId)
                {
                    // A queued fallback already has the corresponding Raw Input release.
                    lastPrintScreenHotkey = fallbackQueued && !printScreenDown ? (int?)null : GetMessageTime();
                    pressHandled = printScreenDown;
                    fallbackQueued = false;
                    capture();
                    return;
                }
                if (selectionKey != null)
                {
                    if (hotkeyId == CancelId) selectionKey(Keys.Escape);
                    if (hotkeyId == SaveId) selectionKey(Keys.Enter);
                    if (hotkeyId == CopyId) selectionKey(Keys.Control | Keys.C);
                }
            }
            else if (message.Msg == RawInputMessage)
            {
                ReadKeyboard(message.LParam);
            }
            else if (message.Msg == CaptureFallbackMessage)
            {
                if (fallbackQueued)
                {
                    fallbackQueued = false;
                    capture();
                }
                return;
            }
            base.WndProc(ref message);
        }

        private void ReadKeyboard(IntPtr input)
        {
            var size = InputSize;
            RawKeyboardInput packet;
            if (GetRawInputData(input, ReadInput, out packet, ref size, HeaderSize) != InputSize ||
                packet.Header.Type != KeyboardType || packet.VirtualKey != PrintScreen)
                return;

            bool modified = GetAsyncKeyState((int)Keys.ShiftKey) < 0 || GetAsyncKeyState((int)Keys.ControlKey) < 0 ||
                GetAsyncKeyState((int)Keys.Menu) < 0 || GetAsyncKeyState((int)Keys.LWin) < 0 ||
                GetAsyncKeyState((int)Keys.RWin) < 0;
            HandlePrintScreen((packet.Flags & Released) != 0, modified, GetMessageTime());
        }

        private void HandlePrintScreen(bool released, bool modified, int messageTime)
        {
            // Windows can put WM_HOTKEY ahead of WM_INPUT for the same keystroke.
            bool recentHotkey = lastPrintScreenHotkey.HasValue &&
                Math.Abs((long)unchecked(messageTime - lastPrintScreenHotkey.Value)) <= SamePressWindowMs;
            if (!released)
            {
                if (!printScreenDown)
                {
                    printScreenDown = true;
                    plainPress = !modified;
                    pressHandled = recentHotkey;
                }
                return;
            }

            bool plain = !modified && (!printScreenDown || plainPress);
            bool handled = pressHandled || recentHotkey;
            if (handled)
                lastPrintScreenHotkey = null;

            printScreenDown = false;
            pressHandled = false;
            if (plain && !handled && !fallbackQueued)
                fallbackQueued = PostMessage(Handle, CaptureFallbackMessage, IntPtr.Zero, IntPtr.Zero);
        }

        private static bool RegisterKeyboard(uint flags, IntPtr window)
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = flags, Target = window };
            return RegisterRawInputDevices(ref device, 1, DeviceSize);
        }

        public void Dispose()
        {
            if (rawInputRegistered)
                RegisterKeyboard(RemoveDevice, IntPtr.Zero);
            if (printScreenRegistered)
                UnregisterHotKey(Handle, PrintScreenId);
            SetSelectionKeys(null);
            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawKeyboardInput
        {
            public RawInputHeader Header;
            public ushort ScanCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VirtualKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        private static extern bool RegisterRawInputDevices(ref RawInputDevice device, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr input, uint command, out RawKeyboardInput data,
            ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        private static extern int GetMessageTime();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }

    internal sealed class SelectionForm : Form
    {
        private readonly Bitmap screen;
        private readonly Rectangle desktop;
        private readonly ToolStrip toolbar;
        private readonly SolidBrush shade = new SolidBrush(Color.FromArgb(145, 8, 12, 19));
        private readonly Pen border = new Pen(Color.FromArgb(93, 238, 209));
        private readonly Font hintFont = new Font("Segoe UI", 11f);
        private Point anchor;
        private bool dragging;

        public Rectangle Area { get; private set; }
        public bool CopyRequested { get; private set; }

        public SelectionForm(Bitmap screen, Rectangle desktop)
        {
            this.screen = screen;
            this.desktop = desktop;
            Text = "PrtShot — выделение области";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = desktop;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;

            toolbar = new ToolStrip
            {
                Visible = false,
                AutoSize = false,
                Dock = DockStyle.None,
                GripStyle = ToolStripGripStyle.Hidden,
                CanOverflow = false,
                BackColor = Color.FromArgb(22, 29, 40),
                Cursor = Cursors.Default
            };
            toolbar.Font = hintFont;
            var save = MakeButton("Сохранить", false);
            var copy = MakeButton("Сохранить и копировать", true);
            toolbar.Items.Add(save);
            toolbar.Items.Add(copy);
            save.AutoSize = false;
            copy.AutoSize = false;
            save.Size = new Size(Math.Max(105, save.GetPreferredSize(Size.Empty).Width + 14), 34);
            copy.Size = new Size(Math.Max(187, copy.GetPreferredSize(Size.Empty).Width + 14), 34);
            toolbar.Size = new Size(save.Width + copy.Width + 12, 44);
            toolbar.MouseUp += CancelOnRightClick;
            Controls.Add(toolbar);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                const int noActivate = 0x08000000;
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= noActivate;
                return parameters;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RaiseWithoutActivation();
        }

        public void RaiseWithoutActivation()
        {
            const uint noMove = 0x0002;
            const uint noSize = 0x0001;
            const uint noActivate = 0x0010;
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, noMove | noSize | noActivate);
        }

        public void EnsureAbove(IntPtr foreground)
        {
            if (!Visible)
                return;

            const uint previousWindow = 3;
            IntPtr above = GetWindow(Handle, previousWindow);
            for (int i = 0; i < 256 && above != IntPtr.Zero; i++)
            {
                if (above == foreground)
                {
                    RaiseWithoutActivation();
                    return;
                }

                above = GetWindow(above, previousWindow);
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int mouseActivate = 0x0021;
            if (message.Msg == mouseActivate)
            {
                const int noActivate = 3;
                message.Result = new IntPtr(noActivate);
                return;
            }
            base.WndProc(ref message);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);

        private ToolStripButton MakeButton(string label, bool copy)
        {
            var button = new ToolStripButton(label)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                BackColor = copy ? Color.FromArgb(93, 238, 209) : Color.FromArgb(48, 61, 77),
                ForeColor = copy ? Color.FromArgb(15, 36, 37) : Color.White
            };
            button.Click += delegate { AcceptSelection(copy); };
            button.MouseUp += CancelOnRightClick;
            return button;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle visible = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, screen.Size));
            e.Graphics.DrawImage(screen, visible, visible, GraphicsUnit.Pixel);
            if (Area.Width > 0 && Area.Height > 0)
            {
                e.Graphics.FillRectangle(shade, 0, 0, ClientSize.Width, Area.Top);
                e.Graphics.FillRectangle(shade, 0, Area.Bottom, ClientSize.Width, ClientSize.Height - Area.Bottom);
                e.Graphics.FillRectangle(shade, 0, Area.Top, Area.Left, Area.Height);
                e.Graphics.FillRectangle(shade, Area.Right, Area.Top, ClientSize.Width - Area.Right, Area.Height);
                e.Graphics.DrawRectangle(border, Area.X, Area.Y, Area.Width - 1, Area.Height - 1);
            }
            else
            {
                e.Graphics.FillRectangle(shade, ClientRectangle);
                if (!dragging)
                    DrawHint(e.Graphics);
            }
        }

        private void DrawHint(Graphics graphics)
        {
            const string text = "Выдели область мышью    ·    Правая кнопка или Esc — отмена";
            Size size = TextRenderer.MeasureText(text, hintFont);
            Rectangle monitor = MonitorAt(Cursor.Position);
            Rectangle badge = new Rectangle(monitor.Left + Math.Max(0, (monitor.Width - size.Width - 20) / 2),
                monitor.Top + 22, size.Width + 20, size.Height + 14);
            using (var background = new SolidBrush(Color.FromArgb(22, 29, 40)))
                graphics.FillRectangle(background, badge);
            TextRenderer.DrawText(graphics, text, hintFont, badge, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            toolbar.Hide();
            anchor = e.Location;
            Area = Rectangle.Empty;
            dragging = true;
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
                UpdateArea(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            CancelOnRightClick(this, e);
            if (e.Button != MouseButtons.Left || !dragging)
                return;

            UpdateArea(e.Location);
            dragging = false;
            Capture = false;
            if (Area.Width > 0 && Area.Height > 0)
            {
                PlaceToolbar(e.Location);
                toolbar.Show();
            }
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture)
            {
                dragging = false;
                Area = Rectangle.Empty;
                Invalidate();
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            return HandleSelectionKeyCore(keys) || base.ProcessCmdKey(ref message, keys);
        }

        public void HandleSelectionKey(Keys keys)
        {
            HandleSelectionKeyCore(keys);
        }

        private bool HandleSelectionKeyCore(Keys keys)
        {
            if (keys == Keys.Escape)
            {
                FinishSelection(DialogResult.Cancel);
                return true;
            }
            if (keys == Keys.Enter || keys == (Keys.Control | Keys.C))
            {
                AcceptSelection(keys != Keys.Enter);
                return true;
            }
            return false;
        }

        private void CancelOnRightClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                FinishSelection(DialogResult.Cancel);
        }

        private void AcceptSelection(bool copy)
        {
            if (dragging || Area.Width <= 0 || Area.Height <= 0)
                return;
            CopyRequested = copy;
            FinishSelection(DialogResult.OK);
        }

        private void FinishSelection(DialogResult result)
        {
            DialogResult = result;
            if (Visible)
                Close();
        }

        private void UpdateArea(Point end)
        {
            Rectangle previous = Area;
            int x = Math.Max(0, Math.Min(ClientSize.Width, end.X));
            int y = Math.Max(0, Math.Min(ClientSize.Height, end.Y));
            Area = Rectangle.FromLTRB(Math.Min(anchor.X, x), Math.Min(anchor.Y, y),
                Math.Max(anchor.X, x), Math.Max(anchor.Y, y));
            Rectangle changed = previous.IsEmpty ? Area : Rectangle.Union(previous, Area);
            changed.Inflate(2, 2);
            Invalidate(changed);
        }

        private Rectangle MonitorAt(Point screenPoint)
        {
            Rectangle monitor = Screen.FromPoint(screenPoint).Bounds;
            monitor.Offset(-desktop.X, -desktop.Y);
            return monitor;
        }

        private void PlaceToolbar(Point end)
        {
            const int gap = 8;
            Rectangle monitor = MonitorAt(PointToScreen(end));
            int x = Math.Max(monitor.Left + gap, Math.Min(Area.Right - toolbar.Width, monitor.Right - toolbar.Width - gap));
            int y = Area.Bottom + gap;
            if (y + toolbar.Height > monitor.Bottom - gap)
                y = Area.Top - toolbar.Height - gap;
            y = Math.Max(monitor.Top + gap, Math.Min(y, monitor.Bottom - toolbar.Height - gap));
            toolbar.Location = new Point(x, y);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                shade.Dispose();
                border.Dispose();
                hintFont.Dispose();
            }
        }
    }

    internal static class DesktopCapture
    {
        private const uint SourceCopyWithLayeredWindows = 0x40CC0020;

        public static Bitmap Take(Rectangle desktop)
        {
            Bitmap bitmap = TakeDesktopPixels(desktop);
            try
            {
                Screen screen = Screen.FromHandle(GetForegroundWindow());
                Rectangle area = Rectangle.Intersect(desktop, screen.Bounds);
                if (area.Width <= 0 || area.Height <= 0)
                    return bitmap;

                var sample = area;
                sample.Offset(-desktop.Left, -desktop.Top);
                if (!IsNearlyBlack(bitmap, sample))
                    return bitmap;

                using (Bitmap gameFrame = DxgiCapture.Take(screen))
                {
                    if (gameFrame != null && gameFrame.Size == screen.Bounds.Size)
                    {
                        Rectangle source = area;
                        source.Offset(-screen.Bounds.Left, -screen.Bounds.Top);
                        using (Graphics graphics = Graphics.FromImage(bitmap))
                            graphics.DrawImage(gameFrame, sample, source, GraphicsUnit.Pixel);
                    }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static bool IsNearlyBlack(Bitmap image, Rectangle area)
        {
            for (int x = 1; x <= 10; x++)
            for (int y = 1; y <= 8; y++)
            {
                Color pixel = image.GetPixel(area.Left + area.Width * x / 11, area.Top + area.Height * y / 9);
                if (pixel.R + pixel.G + pixel.B > 45)
                    return false;
            }
            return true;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        internal static Bitmap TakeDesktopPixels(Rectangle desktop)
        {
            var bitmap = new Bitmap(desktop.Width, desktop.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics target = Graphics.FromImage(bitmap))
                {
                    IntPtr screen = GetDC(IntPtr.Zero);
                    if (screen == IntPtr.Zero)
                        throw new InvalidOperationException("Не удалось получить доступ к рабочему столу.");

                    try
                    {
                        IntPtr destination = target.GetHdc();
                        try
                        {
                            if (!BitBlt(destination, 0, 0, desktop.Width, desktop.Height,
                                screen, desktop.X, desktop.Y, SourceCopyWithLayeredWindows))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        finally { target.ReleaseHdc(destination); }
                    }
                    finally { ReleaseDC(IntPtr.Zero, screen); }
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);
    }

    internal static class ScreenshotFiles
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

        public static string Save(Bitmap bitmap, string folder)
        {
            Directory.CreateDirectory(folder);
            string name = "Screenshot_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
            string destination = Path.Combine(folder, name + ".jpg");
            if (File.Exists(destination))
                destination = Path.Combine(folder, name + "_" + Guid.NewGuid().ToString("N") + ".jpg");

            string temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                    bitmap.Save(temporary, jpeg, parameters);
                }
                File.Move(temporary, destination);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }
}
