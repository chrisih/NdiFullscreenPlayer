#region using directives
using NewTek;
using NewTek.NDI;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
#endregion

namespace dxnditest
{
  class Program
  {
    private static IntPtr _recvInstancePtr;
    private static Thread _receiveThread = null;
    private static bool _exitThread = false;
    private static WindowRenderTarget RenderTarget2D;
    private static SharpDX.Direct2D1.Factory Factory2D;
    private static Finder Finder;
    private static bool _connected;
    private static Form Window;
    private static bool _fullscreen = false;
    private static string _source;

    static void Main(string[] args)
    {
      if(args != null)
      {
        _fullscreen = args.Any(a => string.Equals(a.Trim(), "fullscreen", StringComparison.InvariantCultureIgnoreCase));
        _source = args.FirstOrDefault(a => !string.Equals(a.Trim(), "fullscreen", StringComparison.InvariantCultureIgnoreCase));
      }

      _connected = false;
      if (string.IsNullOrEmpty(_source))
      {
        Console.WriteLine("Waiting for sources...");
        Finder = new Finder(true);
        Finder.Sources.CollectionChanged += Sources_CollectionChanged;
      }
      else
      {
        ConnectNdi(new Source(_source));
      }

      Console.ReadLine();

      DisconnectNdi();
    }

    private static void Sources_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
      if(!_connected)
      {
        _connected = true;
        var src = Finder.Sources.First();
        ConnectNdi(src);
      }
    }

    private static void DisconnectNdi()
    {
      // check for a running thread
      if (_receiveThread != null)
      {
        // tell it to exit
        _exitThread = true;

        // wait for it to end
        _receiveThread.Join();
      }

      // reset thread defaults
      _receiveThread = null;
      _exitThread = false;

      // Destroy the receiver
      NDIlib.recv_destroy(_recvInstancePtr);

      // set it to a safe value
      _recvInstancePtr = IntPtr.Zero;
    }



    private static void ConnectNdi(Source source)
    {
      Console.WriteLine($"Connecting to '{source.Name}'...");

      NDIlib.source_t source_t = new NDIlib.source_t
      {
        p_ndi_name = UTF.StringToUtf8(source.Name)
      };

      NDIlib.recv_create_v3_t recvDescription = new NDIlib.recv_create_v3_t
      {
        source_to_connect_to = source_t,
        color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
        bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
        allow_video_fields = false,
        p_ndi_recv_name = UTF.StringToUtf8("Channel 1")
      };

      // create a new instance connected to this source
      _recvInstancePtr = NDIlib.recv_create_v3(ref recvDescription);

      // free the memory we allocated with StringToUtf8
      Marshal.FreeHGlobal(source_t.p_ndi_name);
      Marshal.FreeHGlobal(recvDescription.p_ndi_recv_name);

      // did it work?
      System.Diagnostics.Debug.Assert(_recvInstancePtr != IntPtr.Zero, "Failed to create NDI receive instance.");

      if (_recvInstancePtr != IntPtr.Zero)
      {
        // start up a thread to receive on
        _receiveThread = new Thread(ReceiveThreadProc) { IsBackground = true, Name = "NdiExampleReceiveThread" };
        _receiveThread.Start();
      }
    }

    private static void InitAndResizeIfNecessary(NDIlib.video_frame_v2_t vf)
    {
      if (Window == null)
      {
        Window = new Form();
        if (_fullscreen)
        {
          Window.ControlBox = false;
          Window.WindowState = FormWindowState.Maximized;
        }
        Window.Show();
      }

      if (Factory2D == null)
        Factory2D = new SharpDX.Direct2D1.Factory();

      if (RenderTarget2D == null)
      {
        var options = new HwndRenderTargetProperties { Hwnd = Window.Handle, PixelSize = new Size2(Window.Width, Window.Height), PresentOptions = PresentOptions.None };
        RenderTarget2D = new WindowRenderTarget(Factory2D, Props, options);
        RenderTarget2D.AntialiasMode = AntialiasMode.PerPrimitive;
      }
    }

    private static RenderTargetProperties Props = new RenderTargetProperties(new PixelFormat(Format.B8G8R8A8_UNorm, SharpDX.Direct2D1.AlphaMode.Premultiplied));

    private static void ReceiveThreadProc()
    {
      while (!_exitThread && _recvInstancePtr != IntPtr.Zero)
      {
        // The descriptors
        NDIlib.video_frame_v2_t videoFrame = new NDIlib.video_frame_v2_t();
        NDIlib.audio_frame_v2_t audioFrame = new NDIlib.audio_frame_v2_t();
        NDIlib.metadata_frame_t metadataFrame = new NDIlib.metadata_frame_t();

        switch (NDIlib.recv_capture_v2(_recvInstancePtr, ref videoFrame, ref audioFrame, ref metadataFrame, 1000))
        {
          // No data
          case NDIlib.frame_type_e.frame_type_none:
            // No data received
            break;

          // frame settings - check for extended functionality
          case NDIlib.frame_type_e.frame_type_status_change:
            break;

          // Video data
          case NDIlib.frame_type_e.frame_type_video:
            if (videoFrame.p_data == IntPtr.Zero)
            {
              // alreays free received frames
              NDIlib.recv_free_video_v2(_recvInstancePtr, ref videoFrame);
              break;
            }

            InitAndResizeIfNecessary(videoFrame);

            int stride = videoFrame.line_stride_in_bytes;

            using (var bmp = new Bitmap(RenderTarget2D, new Size2 { Width = videoFrame.xres, Height = videoFrame.yres }, new DataPointer(videoFrame.p_data, videoFrame.yres * stride), stride, 
              new BitmapProperties
              {
                PixelFormat = RenderTarget2D.PixelFormat,
                DpiX = RenderTarget2D.DotsPerInch.Width,
                DpiY = RenderTarget2D.DotsPerInch.Height
              }))
            {
              bmp.CopyFromMemory(videoFrame.p_data, stride);
              RenderTarget2D.BeginDraw();
              RenderTarget2D.DrawBitmap(bmp, new RawRectangleF { Bottom = 0, Left = 0, Right = RenderTarget2D.Size.Width, Top = RenderTarget2D.Size.Height}, 1, BitmapInterpolationMode.Linear);
              RenderTarget2D.EndDraw();
            }
            
            NDIlib.recv_free_video_v2(_recvInstancePtr, ref videoFrame);
            break;

          case NDIlib.frame_type_e.frame_type_audio:
            NDIlib.recv_free_audio_v2(_recvInstancePtr, ref audioFrame);
            break;
          
          case NDIlib.frame_type_e.frame_type_metadata:
            NDIlib.recv_free_metadata(_recvInstancePtr, ref metadataFrame);
            break;
        }
      }
    }
  }
}
