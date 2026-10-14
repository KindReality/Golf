using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ExperienceX;

// Match Jinx's HALFTONE StretchBlt, then read RGB24. Rotated regions are first
// rectified at source resolution; smoothing occurs only at the final LED resize.
internal sealed class LedCapture : IDisposable
{
    private const uint Copy = 0x00CC0020;
    private sealed class Surface : IDisposable
    {
        public nint Dc { get; }
        public nint Bitmap { get; }
        private readonly nint _old;
        public int Width { get; }
        public int Height { get; }
        public Surface(int width, int height)
        {
            Width=width; Height=height;
            nint desktop=GetDC(0);
            try
            {
                Dc=CreateCompatibleDC(desktop); Bitmap=CreateCompatibleBitmap(desktop,width,height);
                if(Dc==0 || Bitmap==0) { Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                _old=SelectObject(Dc,Bitmap);
            }
            finally { if(desktop!=0) ReleaseDC(0,desktop); }
        }
        public void Dispose() { if(_old!=0) SelectObject(Dc,_old); if(Bitmap!=0) DeleteObject(Bitmap); if(Dc!=0) DeleteDC(Dc); }
    }
    private Surface? _frame, _rectified, _output;
    private readonly Dictionary<(int Width,int Height),Surface> _outputs=[];
    private void Output(int width,int height)
    {
        if(!_outputs.TryGetValue((width,height),out var output))
        {
            if(width is <1 or >65535 || height is <1 or >65535 || (long)width*height>65535) throw new ArgumentException("LED output dimensions are unsupported.");
            if(_outputs.Count>=32) {foreach(var surface in _outputs.Values)surface.Dispose();_outputs.Clear();}
            _outputs[(width,height)]=output=new(width,height);
        }
        _output=output;
    }
    internal nint FrameDc => _frame?.Dc ?? 0;
    internal int Captures { get; private set; }
    internal void Capture(Rectangle bounds)
    {
        Ensure(ref _frame,bounds.Width,bounds.Height);
        nint desktop=GetDC(0);
        try { Check(BitBlt(_frame!.Dc,0,0,bounds.Width,bounds.Height,desktop,bounds.X,bounds.Y,Copy)); ++Captures; }
        finally { if(desktop!=0) ReleaseDC(0,desktop); }
    }
    internal byte[] Sample(PointOptions region, int columns, int rows)
    {
        if(_frame is null) throw new InvalidOperationException("Capture a display first.");
        Output(columns,rows);
        var p=region.Vertices.Select(q=>LedGeometry.Pixel(q,_frame.Width,_frame.Height)).ToArray();
        // Pixel-edge rectangles include the right/bottom source pixel at normalized 1.
        int sw=Math.Max(1,(int)Math.Round(Math.Sqrt(Math.Pow(p[1].X-p[0].X,2)+Math.Pow(p[1].Y-p[0].Y,2)))+1);
        int sh=Math.Max(1,(int)Math.Round(Math.Sqrt(Math.Pow(p[3].X-p[0].X,2)+Math.Pow(p[3].Y-p[0].Y,2)))+1);
        nint source=_frame.Dc; int sx=(int)Math.Round(p[0].X),sy=(int)Math.Round(p[0].Y);
        bool aligned=Math.Abs(p[0].Y-p[1].Y)<1e-6 && Math.Abs(p[0].X-p[3].X)<1e-6 && p[1].X>p[0].X && p[3].Y>p[0].Y;
        if(!aligned || sx<0 || sy<0 || sx+sw>_frame.Width || sy+sh>_frame.Height)
        {
            Ensure(ref _rectified,Math.Min(sw,16384),Math.Min(sh,16384));
            Check(PatBlt(_rectified!.Dc,0,0,_rectified.Width,_rectified.Height,0x00000042));
            double ux=p[1].X-p[0].X,uy=p[1].Y-p[0].Y,vx=p[3].X-p[0].X,vy=p[3].Y-p[0].Y,det=ux*vy-uy*vx;
            if(Math.Abs(det)<1e-9) throw new ArgumentException("Sampling region has no area.");
            NativePoint Transform(double x,double y)
            {
                double dx=x-p[0].X,dy=y-p[0].Y;
                return new((int)Math.Round((dx*vy-dy*vx)/det*(sw-1)),(int)Math.Round((dy*ux-dx*uy)/det*(sh-1)));
            }
            NativePoint[] destination=[Transform(0,0),Transform(_frame.Width,0),Transform(0,_frame.Height)];
            Check(PlgBlt(_rectified.Dc,destination,_frame.Dc,0,0,_frame.Width,_frame.Height,0,0,0));
            source=_rectified.Dc; sx=sy=0; sw=_rectified.Width; sh=_rectified.Height;
        }
        if(SetStretchBltMode(_output!.Dc,4)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(StretchBlt(_output.Dc,0,0,columns,rows,source,sx,sy,sw,sh,Copy));
        return ReadRgb(_output,columns,rows);
    }
    internal byte[] ResizeForTest(Rectangle source, int columns,int rows)
    {
        Output(columns,rows); SetStretchBltMode(_output!.Dc,4);
        Check(StretchBlt(_output.Dc,0,0,columns,rows,FrameDc,source.X,source.Y,source.Width,source.Height,Copy));
        return ReadRgb(_output,columns,rows);
    }
    private static byte[] ReadRgb(Surface surface,int columns,int rows)
    {
        int stride=(columns*3+3)&~3; byte[] dib=new byte[stride*rows];
        var info=new BitmapInfo { Size=40,Width=columns,Height=-rows,Planes=1,Bits=24 };
        // GetDIBits requires the bitmap to be deselected from its memory DC.
        nint dummy=CreateBitmap(1,1,1,1,0),old=SelectObject(surface.Dc,dummy);
        try { if(GetDIBits(surface.Dc,surface.Bitmap,0,(uint)rows,dib,ref info,0)!=rows) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { SelectObject(surface.Dc,old); DeleteObject(dummy); }
        byte[] rgb=new byte[columns*rows*3];
        for(int y=0;y<rows;y++) for(int x=0;x<columns;x++)
        { int source=y*stride+x*3,destination=(y*columns+x)*3; rgb[destination]=dib[source+2]; rgb[destination+1]=dib[source+1]; rgb[destination+2]=dib[source]; }
        return rgb;
    }
    private static void Ensure(ref Surface? surface,int width,int height)
    {
        if(width is <1 or >16384 || height is <1 or >16384 || (long)width*height>40_000_000) throw new ArgumentException("Capture size is unsupported.");
        if(surface?.Width==width && surface.Height==height) return;
        surface?.Dispose(); surface=null; surface=new(width,height);
    }
    private static void Check(bool success) { if(!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public void Dispose() { _frame?.Dispose(); _rectified?.Dispose(); foreach(var surface in _outputs.Values)surface.Dispose();_outputs.Clear();_frame=_rectified=_output=null; }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X,int Y);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width,Height; public ushort Planes,Bits; public uint Compression,ImageSize; public int XPels,YPels; public uint Colors,Important; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateCompatibleBitmap(nint dc,int w,int h);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int w,int h,uint planes,uint bits,nint pixels);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool BitBlt(nint d,int x,int y,int w,int h,nint s,int sx,int sy,uint rop);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool StretchBlt(nint d,int x,int y,int w,int h,nint s,int sx,int sy,int sw,int sh,uint rop);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool PlgBlt(nint d,NativePoint[] points,nint s,int x,int y,int w,int h,nint mask,int mx,int my);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool PatBlt(nint dc,int x,int y,int w,int h,uint rop);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern int SetStretchBltMode(nint dc,int mode);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern int GetDIBits(nint dc,nint bitmap,uint start,uint count,byte[] pixels,ref BitmapInfo info,uint usage);
}
