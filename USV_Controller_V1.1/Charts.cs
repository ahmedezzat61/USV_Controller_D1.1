//using System;
//using System.Diagnostics;
//using System.Runtime.InteropServices;
//using System.Windows;
//using System.Windows.Controls;
//using System.Windows.Interop;
//using System.Windows.Media;
//using System.Windows.Threading;

//namespace USV_Controller_V1._1
//{
//    public class ChartsManager
//    {
//        // █ Win32 API imports for embedding external window
//        [DllImport("user32.dll", SetLastError = true)]
//        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

//        [DllImport("user32.dll", SetLastError = true)]
//        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

//        [DllImport("user32.dll")]
//        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

//        [DllImport("user32.dll")]
//        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

//        [DllImport("user32.dll")]
//        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

//        [DllImport("user32.dll")]
//        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

//        private const int GWL_STYLE = -16;
//        private const int WS_CAPTION = 0x00C00000;
//        private const int WS_THICKFRAME = 0x00040000;
//        private const int WS_MINIMIZE = 0x20000000;
//        private const int WS_MAXIMIZE = 0x01000000;
//        private const int WS_SYSMENU = 0x00080000;
//        private const int SW_SHOWNORMAL = 1;
//        private const int SW_MAXIMIZE = 3;
//        private const uint SWP_FRAMECHANGED = 0x0020;
//        private const uint SWP_NOZORDER = 0x0004;
//        private const uint SWP_NOACTIVATE = 0x0010;

//        // Private fields
//        private Process? _openCpnProcess;
//        private IntPtr _openCpnHandle = IntPtr.Zero;
//        private Window? _chartWindow;
//        private Window _mainWindow;
//        private Action<string> _logCallback;

//        // █ CONFIGURATION - UPDATE THIS PATH TO YOUR OPENCPN INSTALLATION
//        public string OpenCpnPath { get; set; } = @"C:\Program Files (x86)\OpenCPN\OpenCPN.exe";

//        // Constructor
//        public ChartsManager(Window mainWindow, Action<string> logCallback)
//        {
//            _mainWindow = mainWindow;
//            _logCallback = logCallback;
//        }

//        /// <summary>
//        /// Opens the Chart window with OpenCPN embedded
//        /// </summary>
//        public void OpenChartWindow()
//        {
//            if (_chartWindow != null && _chartWindow.IsLoaded)
//            {
//                _chartWindow.Activate();
//                return;
//            }

//            // Create the Chart Window
//            _chartWindow = new Window
//            {
//                Title = "Charts",
//                Width = 400,
//                Height = 300,
//                Owner = _mainWindow, // Makes it a child of main window
//                WindowStartupLocation = WindowStartupLocation.Manual,
//                Left = _mainWindow.Left + _mainWindow.Width + 10, // Position to the right of main window
//                Top = _mainWindow.Top,
//                WindowState = WindowState.Normal
//            };

//            // Create container for OpenCPN
//            Border container = new Border
//            {
//                Background = new SolidColorBrush(Colors.Black),
//                BorderBrush = new SolidColorBrush(Colors.DarkGray),
//                BorderThickness = new Thickness(0), // Remove border thickness
//                HorizontalAlignment = HorizontalAlignment.Stretch,
//                VerticalAlignment = VerticalAlignment.Stretch
//            };

//            // Add placeholder text
//            TextBlock placeholder = new TextBlock
//            {
//                Text = "Launching ...",
//                Foreground = new SolidColorBrush(Colors.White),
//                FontSize = 18,
//                HorizontalAlignment = HorizontalAlignment.Center,
//                VerticalAlignment = VerticalAlignment.Center
//            };
//            container.Child = placeholder;

//            _chartWindow.Content = container;

//            // Handle window closing
//            _chartWindow.Closing += (s, e) =>
//            {
//                CloseOpenCPN();
//            };

//            // Show the window
//            _chartWindow.Show();

//            // Launch OpenCPN after a short delay to let window render
//            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
//            {
//                LaunchAndEmbedOpenCPN(container);
//            }), DispatcherPriority.Loaded);

//            Log("Chart window opened.");
//        }

//        /// <summary>
//        /// Closes the Chart window
//        /// </summary>
//        public void CloseChartWindow()
//        {
//            _chartWindow?.Close();
//            _chartWindow = null;
//        }

//        /// <summary>
//        /// Launches OpenCPN and embeds it in the container
//        /// </summary>
//        private void LaunchAndEmbedOpenCPN(Border containerBorder)
//        {
//            try
//            {
//                // Check if file exists
//                if (!System.IO.File.Exists(OpenCpnPath))
//                {
//                    Log($"OpenCPN not found at: {OpenCpnPath}");

//                    // Update placeholder with error message
//                    _mainWindow.Dispatcher.Invoke(() =>
//                    {
//                        if (containerBorder.Child is TextBlock textBlock)
//                        {
//                            textBlock.Text = $"OpenCPN not found!\n\nPlease install OpenCPN or update the path.\n\nLooking for: {OpenCpnPath}";
//                            textBlock.Foreground = new SolidColorBrush(Colors.Red);
//                        }
//                    });
//                    return;
//                }

//                // Check if already running
//                if (_openCpnProcess != null && !_openCpnProcess.HasExited)
//                {
//                    Log("OpenCPN is already running.");
//                    return;
//                }

//                // Launch OpenCPN
//                ProcessStartInfo startInfo = new ProcessStartInfo
//                {
//                    FileName = OpenCpnPath,
//                    UseShellExecute = true,
//                    WindowStyle = ProcessWindowStyle.Minimized // Start minimized to hide loading
//                };

//                _openCpnProcess = Process.Start(startInfo);

//                if (_openCpnProcess == null)
//                {
//                    Log("Failed to start OpenCPN.");
//                    return;
//                }

//                Log("OpenCPN launched. Waiting for window...");

//                // Wait for the process to create its main window
//                _openCpnProcess.WaitForInputIdle(5000);

//                // Give it extra time to fully load
//                System.Threading.Thread.Sleep(1000);

//                // Get the main window handle
//                _openCpnHandle = _openCpnProcess.MainWindowHandle;

//                if (_openCpnHandle == IntPtr.Zero)
//                {
//                    Log("Could not find OpenCPN window handle. Retrying...");

//                    // Try to find it again after a delay
//                    DispatcherTimer retryTimer = new DispatcherTimer();
//                    retryTimer.Interval = TimeSpan.FromSeconds(1);
//                    int retries = 0;
//                    retryTimer.Tick += (s, e) =>
//                    {
//                        retries++;
//                        _openCpnProcess.Refresh();
//                        _openCpnHandle = _openCpnProcess.MainWindowHandle;

//                        if (_openCpnHandle != IntPtr.Zero)
//                        {
//                            retryTimer.Stop();
//                            // First maximize it before embedding
//                            ShowWindow(_openCpnHandle, SW_MAXIMIZE);
//                            System.Threading.Thread.Sleep(500);
//                            EmbedWindow(containerBorder);
//                        }
//                        else if (retries > 5)
//                        {
//                            retryTimer.Stop();
//                            Log("Failed to find OpenCPN window after multiple attempts.");
//                        }
//                    };
//                    retryTimer.Start();
//                    return;
//                }

//                // First maximize OpenCPN window BEFORE embedding
//                Log("Maximizing OpenCPN before embedding...");
//                ShowWindow(_openCpnHandle, SW_MAXIMIZE);
//                System.Threading.Thread.Sleep(500); // Wait for maximize to complete

//                EmbedWindow(containerBorder);
//            }
//            catch (Exception ex)
//            {
//                Log($"Error launching OpenCPN: {ex.Message}");
//            }
//        }

//        /// <summary>
//        /// Embeds the OpenCPN window into the container
//        /// </summary>
//        private void EmbedWindow(Border containerBorder)
//        {
//            try
//            {
//                Log("Embedding OpenCPN window...");

//                // Get the container's handle
//                HwndSource source = (HwndSource)PresentationSource.FromVisual(containerBorder);
//                if (source == null)
//                {
//                    Log("Could not get container window handle.");
//                    return;
//                }
//                IntPtr containerHandle = source.Handle;

//                // Remove window borders and title bar
//                int style = GetWindowLong(_openCpnHandle, GWL_STYLE);
//                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZE | WS_MAXIMIZE | WS_SYSMENU);
//                SetWindowLong(_openCpnHandle, GWL_STYLE, style);

//                // Force style change to take effect
//                SetWindowPos(_openCpnHandle, IntPtr.Zero, 0, 0, 0, 0,
//                    SWP_FRAMECHANGED | SWP_NOZORDER | SWP_NOACTIVATE);

//                // Set the parent to embed the window
//                SetParent(_openCpnHandle, containerHandle);

//                // Force window to show normally
//                ShowWindow(_openCpnHandle, SW_SHOWNORMAL);

//                System.Threading.Thread.Sleep(200);

//                // Get window client area size
//                _mainWindow.Dispatcher.Invoke(() =>
//                {
//                    // Force container to update layout
//                    containerBorder.UpdateLayout();

//                    int width = (int)_chartWindow.ActualWidth;
//                    int height = (int)_chartWindow.ActualHeight;

//                    // Account for window chrome (title bar, borders)
//                    width -= 16;
//                    height -= 39;

//                    Log($"Resizing OpenCPN to: {width}x{height}");

//                    if (width > 100 && height > 100)
//                    {
//                        MoveWindow(_openCpnHandle, 0, 0, width, height, true);
//                    }
//                    else
//                    {
//                        Log($"Using fallback size: 1200x850");
//                        MoveWindow(_openCpnHandle, 0, 0, 1200, 850, true);
//                    }
//                });

//                Log("OpenCPN embedded successfully!");

//                // Handle container size changes
//                containerBorder.SizeChanged += (s, e) => ResizeEmbeddedWindow(containerBorder);

//                // Setup a timer to aggressively resize for the first few seconds
//                DispatcherTimer resizeTimer = new DispatcherTimer();
//                resizeTimer.Interval = TimeSpan.FromMilliseconds(500);
//                int resizeCount = 0;
//                resizeTimer.Tick += (s, e) =>
//                {
//                    resizeCount++;
//                    ResizeEmbeddedWindow(containerBorder);

//                    if (resizeCount > 6) // Stop after 3 seconds
//                    {
//                        resizeTimer.Stop();
//                    }
//                };
//                resizeTimer.Start();
//            }
//            catch (Exception ex)
//            {
//                Log($"Error embedding window: {ex.Message}");
//            }
//        }

//        /// <summary>
//        /// Resizes the embedded window to fit the container
//        /// </summary>
//        private void ResizeEmbeddedWindow(Border containerBorder)
//        {
//            if (_openCpnHandle != IntPtr.Zero && _chartWindow != null)
//            {
//                _mainWindow.Dispatcher.Invoke(() =>
//                {
//                    // Use the actual window client area
//                    int width = (int)_chartWindow.ActualWidth - 16;
//                    int height = (int)_chartWindow.ActualHeight - 39;

//                    // Only resize if we have valid dimensions
//                    if (width > 100 && height > 100)
//                    {
//                        MoveWindow(_openCpnHandle, 0, 0, width, height, true);
//                    }
//                });
//            }
//        }

//        /// <summary>
//        /// Closes OpenCPN process
//        /// </summary>
//        public void CloseOpenCPN()
//        {
//            try
//            {
//                if (_openCpnProcess != null && !_openCpnProcess.HasExited)
//                {
//                    _openCpnProcess.CloseMainWindow();
//                    _openCpnProcess.WaitForExit(3000);

//                    if (!_openCpnProcess.HasExited)
//                    {
//                        _openCpnProcess.Kill();
//                    }

//                    _openCpnProcess.Dispose();
//                    _openCpnProcess = null;
//                    _openCpnHandle = IntPtr.Zero;

//                    Log("OpenCPN closed.");
//                }
//            }
//            catch (Exception ex)
//            {
//                Log($"Error closing OpenCPN: {ex.Message}");
//            }
//        }

//        /// <summary>
//        /// Checks if the Chart window is currently open
//        /// </summary>
//        public bool IsChartWindowOpen()
//        {
//            return _chartWindow != null && _chartWindow.IsLoaded;
//        }

//        /// <summary>
//        /// Checks if OpenCPN is currently running
//        /// </summary>
//        public bool IsOpenCpnRunning()
//        {
//            return _openCpnProcess != null && !_openCpnProcess.HasExited;
//        }

//        /// <summary>
//        /// Cleanup method - call this when main window closes
//        /// </summary>
//        public void Cleanup()
//        {
//            CloseChartWindow();
//            CloseOpenCPN();
//        }

//        /// <summary>
//        /// Logs a message using the callback
//        /// </summary>
//        private void Log(string message)
//        {
//            _logCallback?.Invoke(message);
//        }
//    }
//}