using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Berkelium.Managed;

using UserControl       = System.Windows.Controls.UserControl;
using MouseEventArgs    = System.Windows.Input.MouseEventArgs;
using KeyEventArgs      = System.Windows.Input.KeyEventArgs;
using Application       = System.Windows.Application;
using Rect              = Berkelium.Managed.Rect;
using Rectangle         = System.Drawing.Rectangle;
using Cursors           = System.Windows.Input.Cursors;



namespace WPFChrome
{
    /// <summary>
    /// Interaction logic for WebKitFrame.xaml
    /// </summary>
    public partial class WebKitFrame : UserControl
    {
        #region Static

        static internal bool _isInit;

        static WebKitFrame()
        {
            _isInit = false;
        }

        #endregion        

        #region Variables

        protected Context Context;
        protected Berkelium.Managed.Window Window;
        protected Bitmap WindowBitmap;

        #endregion        

        #region Methods

        /// <summary>
        /// Constructor
        /// </summary>
        public WebKitFrame()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Wire up event handlers
        /// </summary>
        protected void WireEventHandlers()
        {
            foreach (var method in this.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (!method.Name.StartsWith("WebKit_"))
                    continue;

                var eventName = method.Name.Replace("WebKit_", "");

                var evt = Window.GetType().GetEvent(eventName);

                evt.AddEventHandler(Window, Delegate.CreateDelegate(evt.EventHandlerType, this, method));
            }
        }

        /// <summary>
        /// Begins the update loop
        /// </summary>
        private void StartLoop()
        {
            DispatcherTimer dispatcherTimer = new DispatcherTimer();
            dispatcherTimer.Tick += new EventHandler(Update);
            dispatcherTimer.Interval = new TimeSpan(0, 0, 0, 0, 20); // 20ms
            dispatcherTimer.Start();
        }

        // It might appear that this function doesn't work, but it actually does - web pages
        //  that handle multiple buttons get all the right events. We just don't see Chrome
        //  context menus and such because Berkelium doesn't implement them yet.
        internal static Berkelium.Managed.MouseButton MapMouseButton(System.Windows.Input.MouseButton button)
        {
            switch (button)
            {
                default:
                case System.Windows.Input.MouseButton.Left:
                    return Berkelium.Managed.MouseButton.Left;
                case System.Windows.Input.MouseButton.Middle:
                    return Berkelium.Managed.MouseButton.Middle;
                case System.Windows.Input.MouseButton.Right:
                    return Berkelium.Managed.MouseButton.Right;
            }
        }

        /// <summary>
        /// Handles the paint.
        /// </summary>
        unsafe internal void HandlePaintEvent(IntPtr sourceBuffer, Rect rect, int dx, int dy, Rect scrollRect)
        {
            System.Drawing.Imaging.BitmapData sourceData;
            var clientRect = new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height);
            byte[] TemporaryBuffer = null;

            if (dx != 0 || dy != 0)
            {
                var sourceRect = new Rectangle(scrollRect.Left, scrollRect.Top, scrollRect.Width, scrollRect.Height);
                var destRect = sourceRect;
                destRect.X += dx;
                destRect.Y += dy;

                // We want to only draw the overlapping portion of the scrolled and unscrolled
                //  rectangles, since the scrolled rectangle is probably partially offscreen
                var overlap = Rectangle.Intersect(destRect, sourceRect);

                // We need to handle scrolling to the left
                if (destRect.Left < 0)
                {
                    sourceRect.X -= destRect.Left;
                    destRect.X = 0;
                }
                // And upward
                if (destRect.Top < 0)
                {
                    sourceRect.Y -= destRect.Top;
                    destRect.Y = 0;
                }

                destRect.Width = sourceRect.Width = overlap.Width;
                destRect.Height = sourceRect.Height = overlap.Height;

                // If the clipping calculations resulted in a rect that contains zero pixels, 
                //  don't bother trying to do the blit.
                if ((sourceRect.Width > 0) && (sourceRect.Height > 0))
                {
                    sourceData = WindowBitmap.LockBits(
                        sourceRect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppRgb
                    );

                    int totalSize = sourceData.Stride * sourceData.Height;

                    if ((TemporaryBuffer == null) || (totalSize > TemporaryBuffer.Length))
                        TemporaryBuffer = new byte[totalSize];

                    Marshal.Copy(sourceData.Scan0, TemporaryBuffer, 0, totalSize);
                    WindowBitmap.UnlockBits(sourceData);

                    fixed (byte* ptr = &(TemporaryBuffer[0]))
                    {
                        sourceData.Scan0 = new IntPtr(ptr);

                        var destData = WindowBitmap.LockBits(
                            destRect, System.Drawing.Imaging.ImageLockMode.WriteOnly | System.Drawing.Imaging.ImageLockMode.UserInputBuffer,
                            System.Drawing.Imaging.PixelFormat.Format32bppRgb, sourceData
                        );

                        WindowBitmap.UnlockBits(destData);
                    }

                    InvalidateBerkelium();
                }
            }

            // If we get a paint event after a resize, the rect can be larger than the buffer.
            if ((clientRect.Right > WindowBitmap.Width) || (clientRect.Bottom > WindowBitmap.Height))
                return;

            sourceData = new System.Drawing.Imaging.BitmapData();
            sourceData.Width = clientRect.Width;
            sourceData.Height = clientRect.Height;
            sourceData.PixelFormat = System.Drawing.Imaging.PixelFormat.Format32bppRgb;
            sourceData.Stride = clientRect.Width * 4;
            sourceData.Scan0 = sourceBuffer;

            // Sometimes this can fail if we process an old paint event after we
            //  request a resize, so we just eat the exception in that case.
            // Yes, this is terrible.
            //Bitmap windowBitmap = new Bitmap(clientRect.Width, clientRect.Height);
            try
            {
                // This oddball form of LockBits performs a write to the bitmap's
                //  internal buffer by copying from another BitmapData you pass in.
                // In this case we're passing in the source buffer.
                var bd = WindowBitmap.LockBits(
                    clientRect,
                    System.Drawing.Imaging.ImageLockMode.WriteOnly | System.Drawing.Imaging.ImageLockMode.UserInputBuffer,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb, sourceData
                );

                // For some reason we still have to unlock the bits afterward.
                WindowBitmap.UnlockBits(bd);

                InvalidateBerkelium();
            }
            catch
            {
            }
        }

        private void InvalidateBerkelium()
        {
            BitmapImage bmpi = new BitmapImage();
            bmpi.BeginInit();
            bmpi.StreamSource = new MemoryStream();
            WindowBitmap.Save(bmpi.StreamSource, System.Drawing.Imaging.ImageFormat.Bmp);
            bmpi.EndInit();
            _buffer.Source = bmpi;
        }

        #endregion             

        #region Events

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            BerkeliumSharp.Init( Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) );

            BerkeliumSharp.PureCall += OnPureCall;
            BerkeliumSharp.OutOfMemory += OnOutOfMemory;
            BerkeliumSharp.InvalidParameter += OnInvalidParameter;
            BerkeliumSharp.Assertion += OnAssertion;

            Application.Current.Exit += OnExit;

            if (Window == null)
            {
                Context = Context.Create();
                Window = new Berkelium.Managed.Window(Context);
            }
            else
            {
                Context = Window.Context;
            }

            WireEventHandlers();

            UserControl_SizeChanged(null, null);
          
            Window.NavigateTo("http://www.google.com");
            Window.Widget.Focus();

            StartLoop();

            _isInit = true;
        }

        private void UserControl_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            Window.Widget.MouseMoved((int)p.X, (int)p.Y);
        }

        private void UserControl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Window.Widget.MouseButton(MapMouseButton(e.ChangedButton), true);
        }

        private void UserControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Window.Widget.MouseButton(MapMouseButton(e.ChangedButton), false);
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Window == null)
                return;

            int width = Math.Max(1, (int)ActualWidth);
            int height = Math.Max(1, (int)ActualHeight);

            if (WindowBitmap != null)
                WindowBitmap.Dispose();
            WindowBitmap = new Bitmap(width, height);

            Window.Resize(width, height);
        }

        protected void Update(object sender, EventArgs e)
        {
            if (_isInit)
            {
                BerkeliumSharp.Update();
            }
        }

        protected void OnExit(object sender, EventArgs e)
        {
            if (_isInit)
            {
                if (Window != null)
                    Window.Dispose();
                Window = null;
                if (WindowBitmap != null)
                    WindowBitmap.Dispose();
                WindowBitmap = null;

                BerkeliumSharp.Destroy();
                _isInit = false;
            }
        }

        #endregion

        #region BerkeliumEvents

        protected static void OnPureCall()
        {
            throw new ApplicationException("Pure virtual call in Berkelium");
        }

        protected static void OnOutOfMemory()
        {
            throw new ApplicationException("Allocation in Berkelium");
        }

        protected static void OnAssertion(string assertionMessage)
        {
            throw new ApplicationException(String.Format("Assertion failed: {0}", assertionMessage));
        }

        protected static void OnInvalidParameter(string expression, string function, string file, int lineNumber)
        {
            throw new ApplicationException(String.Format(
                "Invalid parameter in Berkelium: {0} at line {3} in function {1} in file {2}",
                expression, function, file, lineNumber
            ));
        }

        private void WebKit_Paint(Berkelium.Managed.Window window, IntPtr sourceBuffer, Rect rect, int dx, int dy, Rect scrollRect)
        {
            HandlePaintEvent(sourceBuffer, rect, dx, dy, scrollRect);
        }

        private void WebKit_NavigationRequested(Berkelium.Managed.Window source, string url, string referrer, bool isNewWindow, ref bool cancelDefaultAction)
        {
            if (isNewWindow)
            {
                cancelDefaultAction = true;
                Console.WriteLine("WebKitFrame: Prevented a new window from opening.");
            }
        }

        private void WebKit_CursorChanged(Berkelium.Managed.Window window, IntPtr cursorHandle)
        {
            if (cursorHandle != IntPtr.Zero)
            {
                switch(cursorHandle.ToInt32())
                {
                    case 65573: this.Cursor = Cursors.Hand; break;
                    case 65545: this.Cursor = Cursors.Arrow; break;
                    case 65547: this.Cursor = Cursors.IBeam; break;
                }
            }
            else
                this.Cursor = null;
        }

        #endregion
    }
}
