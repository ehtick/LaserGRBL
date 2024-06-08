﻿using LaserGRBL.Obj3D;
using SharpGL;
using SharpGL.SceneGraph;
using SharpGL.SceneGraph.Cameras;
using SharpGL.Version;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static LaserGRBL.ProgramRange;

namespace LaserGRBL.UserControls
{
	[ToolboxBitmap(typeof(SceneControl), "GrblScene")]
	public partial class GrblPanel3D : UserControl, IGrblPanel
	{
		// last mouse position
		private Point? mLastMousePos = null;
		// current mouse world position
		private PointF? mMouseWorldPosition { get; set; }
		// main camera
		private OrthographicCamera mCamera { get; } = new OrthographicCamera();
		private GLColor mBackgroundColor { get; set; }
		private GLColor mTicksColor { get; set; }
		private GLColor mMinorsColor { get; set; }
		private GLColor mOriginsColor { get; set; }
		private GLColor mPointerColor { get; set; }
		private GLColor mTextColor { get; set; }
		// default font
		private const string mFontName = "Courier New";
		private Font mTextFont { get; set; } = new Font(mFontName, 12);
		// background
		private Grid3D mGrid = null;
		// grbl object
		private Grbl3D mGrbl3D = null;
		private Grbl3D mGrbl3DOff = null;
		private object mGrbl3DLock = new object();
		// reload request
		private bool mReload = false;
		// invalidate all request
		private bool mInvalidateAll = false;
		// viewport padding
		private Padding mPadding = new Padding(50, 0, 0, 30);
		// grbl core
		private GrblCore Core;
		public float PointerSize { get; set; } = 3;
		// last control size
		private PointF mLastControlSize;
		// drawing thread
		private Tools.ThreadObject mThreadDraw;
		// generated 3d bitmap
		private Bitmap mBmp = null;
		// critical section
		private object mBitmapLock = new object();
		// open gl object
		private OpenGL OpenGL;
		// rulers steps
		private List<KeyValuePair<double, int>> mRulerSteps = new List<KeyValuePair<double, int>> {
			new KeyValuePair<double, int>(   100,    5),
			new KeyValuePair<double, int>(   200,   10),
			new KeyValuePair<double, int>(   600,   30),
			new KeyValuePair<double, int>(  1000,   50),
			new KeyValuePair<double, int>(  2000,  100),
			new KeyValuePair<double, int>(  6000,  300),
			new KeyValuePair<double, int>( 10000,  500),
			new KeyValuePair<double, int>( 20000, 1000),
			new KeyValuePair<double, int>( 60000, 3000),
			new KeyValuePair<double, int>(100000, 5000)
		};
		private GPoint mLastWPos;
		private GPoint mLastMPos;
		private float mCurF;
		private float mCurS;
		bool forcez = false;
		private bool mFSTrig;

		private Base.Mathematics.MobileDAverageCalculator RenderTime;
		private Base.Mathematics.MobileDAverageCalculator RefreshRate;

		private static Exception FatalException;
		private Color IssueMatchBackColor;

		public static string CurrentRendererType = "";
		public static string CurrentVendor = "";
		public static string CurrentRenderer = "";
		public static string CurrentGLVersion = "";

		private static string FirstGlError = null;

		public static string GlDiagnosticMessage
		{
			get
			{
				if (FatalException != null)
					return FatalException.Message;
				else
					return FirstGlError;
			}
		}

		public GrblPanel3D()
		{
			InitializeComponent();
			IssueMatchBackColor = Color.FromArgb(BackColor.A, BackColor.R, BackColor.G, BackColor.B);
			RenderTime = new Base.Mathematics.MobileDAverageCalculator(30);
			RefreshRate = new Base.Mathematics.MobileDAverageCalculator(30);
			mLastWPos = GPoint.Zero;
			mLastMPos = GPoint.Zero;
			forcez = Settings.GetObject("Enale Z Jog Control", false);
			MouseDoubleClick += GrblPanel3D_DoubleClick;
			SetStyle(ControlStyles.AllPaintingInWmPaint, true);
			SetStyle(ControlStyles.UserPaint, true);
			SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
			MouseWheel += GrblSceneControl_MouseWheel;
			MouseDown += GrblSceneControl_MouseDown;
			MouseMove += GrblSceneControl_MouseMove;
			MouseUp += GrblSceneControl_MouseUp;
			MouseLeave += GrblSceneControl_MouseLeave;
			Resize += GrblPanel3D_Resize;
			Disposed += GrblPanel3D_Disposed;
			mCamera.Position = new Vertex(0, 0, 0);
			mCamera.Near = 0;
			mCamera.Far = 100000000;
			AutoSizeDrawing();
			mLastControlSize = new PointF(Width, Height);
			mGrid = new Grid3D();

			Task.Factory.StartNew(() =>
			{
				GlThread();
			});
		}

		[System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptions]
		private void GlThread()
		{
			try
			{
				InitializeOpenGL();
				while (true)
				{
					DrawScene();
					Thread.Sleep(30);
				}
			}
			catch (Exception ex)
			{
				Logger.LogException("OpenGL", ex);
				FatalException = ex;
				Invalidate();
				ExceptionManager.OnHandledException(ex, true);
			}
		}

		private void SetWorldPosition(double left, double right, double bottom, double top)
		{
			// max viewport size
			const double max = 50000;
			// compute size
			double width = right - left;
			double height = top - bottom;
			// exit if max viewport reached
			if (width > max * 2 || height > max * 2) return;
			// fix min and max
			if (left < -max)
			{
				left = -max;
				right = left + width;
			}
			if (right > max)
			{
				right = max;
				left = max - width;
			}
			if (bottom < -max)
			{
				bottom = -max;
				top = bottom + height;
			}
			if (top > max)
			{
				top = max;
				bottom = top - height;
			}
			// set new camera coords
			mCamera.Left = left;
			mCamera.Right = right;
			mCamera.Bottom = bottom;
			mCamera.Top = top;
		}

		private void GrblPanel3D_Resize(object sender, EventArgs e)
		{
			// compute ratiobesed on last size
			double wRatio = Width / mLastControlSize.X;
			double hRatio = Height / mLastControlSize.Y;
			// define max ratio
			double max = Math.Max(Math.Abs(wRatio), Math.Abs(hRatio));
			// normalize ratio (only values less than 1 are valid)
			wRatio = wRatio / max;
			hRatio = hRatio / max;
			// set world positions
			SetWorldPosition(mCamera.Left * wRatio, mCamera.Right * wRatio, mCamera.Bottom * hRatio, mCamera.Top * hRatio);
			// save last size
			mLastControlSize = new PointF(Width, Height);
		}

		private void GrblPanel3D_DoubleClick(object sender, MouseEventArgs e)
		{
			if (Settings.GetObject("Click N Jog", true) && mMouseWorldPosition != null)
			{
				Core.BeginJog((PointF)mMouseWorldPosition, e.Button == MouseButtons.Right);
			}
		}

		protected void InitializeOpenGL()
		{
			object parameter = null;
			OpenGL = new OpenGL();


			if (Settings.UseSoftwareOpenGL)
			{
				CurrentRendererType = "DIB!";
				OpenGL.Create(OpenGLVersion.OpenGL2_1, RenderContextType.DIBSection, Width, Height, 32, parameter);
				CheckError(OpenGL, "Create");
			}
			else
			{ 
				try
				{

					CurrentRendererType = "FBO";
					OpenGL.Create(OpenGLVersion.OpenGL2_1, RenderContextType.FBO, Width, Height, 32, parameter);
					if (OpenGL.GetError() != OpenGL.GL_NO_ERROR) throw new Exception("Cannot Create FBO");
				}
				catch
				{
					CurrentRendererType = "DIB";
					OpenGL.Create(OpenGLVersion.OpenGL2_1, RenderContextType.DIBSection, Width, Height, 32, parameter);
					CheckError(OpenGL, "Create");
				}
			}

			try { CurrentVendor = OpenGL.Vendor; } catch { CurrentVendor = "Unknown"; }
			try { CurrentRenderer = OpenGL.Renderer; } catch { CurrentRenderer = "Unknown"; }
			try { CurrentGLVersion = OpenGL.Version; } catch { CurrentGLVersion = "0.0"; }

			Logger.LogMessage("OpenGL", "{0} OpenGL {1}, {2}, {3}", CurrentRendererType, CurrentGLVersion, CurrentVendor, CurrentRenderer);

			OpenGL.ShadeModel(OpenGL.GL_SMOOTH);
			CheckError(OpenGL, "ShadeModel");
			OpenGL.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
			OpenGL.ClearDepth(1.0f);
			OpenGL.Enable(OpenGL.GL_DEPTH_TEST);
			CheckError(OpenGL, "DepthTest");
			OpenGL.DepthFunc(OpenGL.GL_LEQUAL);
			CheckError(OpenGL, "DepthFunc");
			OpenGL.Hint(OpenGL.GL_PERSPECTIVE_CORRECTION_HINT, OpenGL.GL_NICEST);
			CheckError(OpenGL, "Hint");
		}

		static uint errcounter = 0;
		public static bool CheckError(OpenGL gl, string action)
		{
			uint err = gl.GetError();
			if (err != OpenGL.GL_NO_ERROR)
			{

				if (errcounter < 3)
				{
					errcounter++;

					string message = string.Format("{0}: {1} [{2}]", action, gl.GetErrorDescription(err), err);
					if (errcounter == 1) FirstGlError = message;
					Logger.LogMessage("OpenGL", message);
				}
			}
			return err != OpenGL.GL_NO_ERROR;
		}

		private void GrblPanel3D_Disposed(object sender, EventArgs e)
		{
			DisposeGrbl3D();
			mThreadDraw?.Dispose();
		}

		private void DrawPointer()
		{
			if (Core == null) return;
			// draw laser cross
			GLColor color = new GLColor();
			color.FromColor(mPointerColor);
			OpenGL.Color(color);
			OpenGL.LineWidth(PointerSize);
			OpenGL.Begin(OpenGL.GL_LINES);
			Vertex pointerPos = new Vertex(Core.WorkPosition.X, Core.WorkPosition.Y, 0.2f);
			OpenGL.Vertex(pointerPos.X - 2, pointerPos.Y, pointerPos.Z);
			OpenGL.Vertex(pointerPos.X + 2, pointerPos.Y, pointerPos.Z);
			OpenGL.End();
			OpenGL.Begin(OpenGL.GL_LINES);
			OpenGL.Vertex(pointerPos.X, pointerPos.Y - 2, pointerPos.Z);
			OpenGL.Vertex(pointerPos.X, pointerPos.Y + 2, pointerPos.Z);
			OpenGL.End();
			OpenGL.Flush();
		}

		public void DrawRulers()
		{
			// clear left ruler background
			OpenGL.Enable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.Scissor(0, 0, mPadding.Left, Height);
			OpenGL.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT | OpenGL.GL_STENCIL_BUFFER_BIT);
			OpenGL.Disable(OpenGL.GL_SCISSOR_TEST);
			// clear bottom ruler background
			OpenGL.Enable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.Scissor(0, 0, Width, mPadding.Bottom);
			OpenGL.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT | OpenGL.GL_STENCIL_BUFFER_BIT);
			OpenGL.Disable(OpenGL.GL_SCISSOR_TEST);
			// clear right ruler background
			OpenGL.Enable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.Scissor(Width - mPadding.Right, 0, mPadding.Right, Height);
			OpenGL.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT | OpenGL.GL_STENCIL_BUFFER_BIT);
			OpenGL.Disable(OpenGL.GL_SCISSOR_TEST);
			// clear top ruler background
			OpenGL.Enable(OpenGL.GL_SCISSOR_TEST);
			OpenGL.Scissor(0, Height - mPadding.Top, Width, mPadding.Top);
			OpenGL.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT | OpenGL.GL_STENCIL_BUFFER_BIT);
			OpenGL.Disable(OpenGL.GL_SCISSOR_TEST);
			// get world width
			double worldWidth = mCamera.Right - mCamera.Left;
			// init rulers step
			int step = 200;
			// get step from list
			foreach (KeyValuePair<double, int> rulerStep in mRulerSteps)
			{
				if (worldWidth < rulerStep.Key)
				{
					step = rulerStep.Value;
					break;
				}
			}
			// get ratio
			double wRatio = Width / (mCamera.Right - mCamera.Left);
			// draw horizontal
			for (int i = (int)mCamera.Left + (int)(mPadding.Left / wRatio); i < (int)mCamera.Right - (int)(mPadding.Right / wRatio); i += 1)
			{
				if (i % step == 0)
				{
					string text = $"{i}";
					Size size = TextRenderer.MeasureText(text, mTextFont);
					SizeF sizeProp = new SizeF(size.Width * 0.8f, size.Height * 0.8f);
					double x = i * wRatio - mCamera.Left * wRatio - sizeProp.Width / 4f;
					OpenGL.DrawText((int)x, mPadding.Bottom - size.Height, mTextColor.R, mTextColor.G, mTextColor.B, mTextFont.FontFamily.Name, mTextFont.Size, text);
				}
			}
			// get ratio
			double hRatio = Height / (mCamera.Top - mCamera.Bottom);
			// draw vertical
			for (int i = (int)mCamera.Bottom + (int)(mPadding.Bottom / hRatio); i < (int)mCamera.Top - (int)(mPadding.Top / hRatio); i += 1)
			{
				if (i % step == 0)
				{
					string text = $"{i}";
					Size size = TextRenderer.MeasureText(text, mTextFont);
					SizeF sizeProp = new SizeF(size.Width * 0.8f, size.Height * 0.8f);
					double x = mPadding.Left - sizeProp.Width;
					double y = i * hRatio - mCamera.Bottom * hRatio - sizeProp.Height / 4f;
					OpenGL.DrawText((int)x, (int)y, mTextColor.R, mTextColor.G, mTextColor.B, mTextFont.FontFamily.Name, mTextFont.Size, text);
				}
			}
			OpenGL.Flush();
		}

		private void DrawScene()
		{
			Tools.SimpleCrono crono = new Tools.SimpleCrono(true);
			if (mBackgroundColor == null) return;
			OpenGL.MakeCurrent();
			CheckError(OpenGL, "MakeCurrent");
			OpenGL.SetDimensions(Width, Height);
			OpenGL.Viewport(0, 0, Width, Height);
			mCamera.Project(OpenGL);
			CheckError(OpenGL, "Viewport");
			OpenGL.ClearColor(mBackgroundColor.R, mBackgroundColor.G, mBackgroundColor.B, mBackgroundColor.A);
			OpenGL.Clear(OpenGL.GL_COLOR_BUFFER_BIT | OpenGL.GL_DEPTH_BUFFER_BIT | OpenGL.GL_STENCIL_BUFFER_BIT);
			CheckError(OpenGL, "Clear");
			// render grid
			mGrid.ShowMinor = mCamera.Right - mCamera.Left < 100;
			mGrid.TicksColor = mTicksColor;
			mGrid.MinorsColor = mMinorsColor;
			mGrid.OriginsColor = mOriginsColor;
			mGrid.Render(OpenGL);
			CheckError(OpenGL, "RenderGrid");
			// enable anti alias
			OpenGL.Enable(OpenGL.GL_BLEND);
			OpenGL.BlendFunc(OpenGL.GL_SRC_ALPHA, OpenGL.GL_ONE_MINUS_SRC_ALPHA);
			CheckError(OpenGL, "Blend");
			OpenGL.Enable(OpenGL.GL_LINE_SMOOTH);
			OpenGL.Hint(OpenGL.GL_LINE_SMOOTH_HINT, OpenGL.GL_NICEST);
			CheckError(OpenGL, "Smooth");
			// manage grbl object
			if (mReload)
			{
				mReload = false;
				lock (mGrbl3DLock)
				{
					mGrbl3D?.Dispose();
					mGrbl3DOff?.Dispose();
					mGrbl3D = new Grbl3D(Core, "LaserOn", false, ColorScheme.PreviewLaserPower);
					mGrbl3DOff = new Grbl3D(Core, "LaserOff", true, ColorScheme.PreviewOtherMovement);
				}
				mGrbl3DOff.LineWidth = 1;
			}
			if (mGrbl3D != null)
			{
				if (mInvalidateAll)
				{
					mInvalidateAll = false;
					mGrbl3D.Color = ColorScheme.PreviewLaserPower;
					mGrbl3D.InvalidateAll();
					mGrbl3DOff.Color = ColorScheme.PreviewOtherMovement;
					mGrbl3DOff.InvalidateAll();
				}
				if (Core.ShowLaserOffMovements.Value)
				{
					mGrbl3DOff.Invalidate();
					mGrbl3DOff.Render(OpenGL);
				}
				mGrbl3D.Invalidate();
				mGrbl3D.Render(OpenGL);
			}
			CheckError(OpenGL, "RenderObject");
			// disable anti alias
			OpenGL.Disable(OpenGL.GL_BLEND);
			OpenGL.Disable(OpenGL.GL_LINE_SMOOTH);
			CheckError(OpenGL, "BlendDisable");
			// main hud
			DrawPointer();
			CheckError(OpenGL, "Pointer");
			DrawRulers();
			CheckError(OpenGL, "Rulers");
			OpenGL.Flush();
			CheckError(OpenGL, "Flush");
			// enter lock and copy bitmap
			lock (mBitmapLock)
			{
				// new bitmap if different size
				if (mBmp == null || mBmp.Width != Width || mBmp.Height != Height)
				{
					mBmp?.Dispose();
					mBmp = new Bitmap(Width, Height);
				}
				// clone opengl graphics
				using (Graphics g = Graphics.FromImage(mBmp))
				{
					IntPtr handleDeviceContext = g.GetHdc();
					OpenGL.Blit(handleDeviceContext);
					CheckError(OpenGL, "Blit");
					g.ReleaseHdc(handleDeviceContext);
				}
			}
			RenderTime.EnqueueNewSample(crono.ElapsedTime.TotalMilliseconds);

			// call control invalidate
			Invalidate();
		}

		Tools.SimpleCrono FpsCrono;
		protected override void OnPaint(PaintEventArgs e)
		{
			Exception OnPaintException = null;

			if (FpsCrono != null) RefreshRate.EnqueueNewSample(FpsCrono.ElapsedTime.TotalMilliseconds); //compute time distance between two OnPaint
			FpsCrono = new Tools.SimpleCrono(true);


			if (mBmp != null)
			{
				try
				{
					// enter lock and copy bitmap to control
					lock (mBitmapLock)
					{
						e.Graphics.DrawImage(mBmp, new Point(0, 0));
					}
					DoGDIDraw(e);
				}
				catch (Exception ex) { OnPaintException = ex;  }
			}
			else
			{
				// nothing to draw from GL Thread
			}


			if (FatalException != null)
				DrawException(e, FatalException.ToString());
			else if (OnPaintException != null)
				DrawException(e, OnPaintException.ToString());
			else if (mBmp == null)
				DrawException(e, "nothing to draw");
		}

		private void DrawException(PaintEventArgs e, string text)
		{
			Size size = MeasureText(text, Font);
			Size size2 = new Size(size.Width + 20, size.Height);
			Point point = new Point((Width - size2.Width) / 2, (Height - size2.Height) / 2);
			DrawOverlay(e, point, size2, Color.Red, 100);
			e.Graphics.DrawString(text, Font, Brushes.White, point.X, point.Y);
		}

		private string FormatCoord(float coord)
		{
			return string.Format("{0,10:######0.000}", coord);
		}

		private void DoGDIDraw(PaintEventArgs e)
		{
			if (Core == null) return;
			const int top = 12;
			using (Brush b = new SolidBrush(ColorScheme.PreviewText))
			using (Font font = new Font(mFontName, 10))
			{
				// set aliasing
				e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
				// use z
				bool useZ = mLastWPos.Z != 0 || mLastMPos.Z != 0 || forcez;
				string text = "         X          Y";
				if (useZ) text += "          Z    ";
				// last position
				text += $"\nMCO {FormatCoord(mLastMPos.X)} {FormatCoord(mLastMPos.Y)}";
				if (useZ) text += $" {FormatCoord(mLastMPos.Z)}";
				// working offset
				if (Core.WorkingOffset != GPoint.Zero)
				{
					text += $"\nWCO {FormatCoord(mLastWPos.X)} {FormatCoord(mLastWPos.Y)}";
					if (useZ) text += $" {FormatCoord(mLastWPos.Z)}";
				}
				// mouse info
				if (mMouseWorldPosition != null)
				{
					PointF pos = (PointF)mMouseWorldPosition;
					text += $"\nPTR {FormatCoord(pos.X)} {FormatCoord(pos.Y)}";
					if (useZ) text += "          ";
				}
				Size size = MeasureText(text, font);
				Point point = new Point(Width - size.Width - mPadding.Right, top);
				DrawOverlay(e, point, size, ColorScheme.PreviewRuler, 100);
				e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
				e.Graphics.DrawString(text, font, b, point.X, point.Y);

				// laser info
				if (mCurF != 0 || mCurS != 0 || mFSTrig)
				{
					mFSTrig = true;
					int pos = point.Y + size.Height + 5;
					text = $"F {string.Format("{0,6:####0.##}", mCurF)}\nS {string.Format("{0,6:##0.##}", mCurS)}";
					size = MeasureText(text, font);
					point = new Point(Width - size.Width - mPadding.Right, pos);
					DrawOverlay(e, point, size, ColorScheme.PreviewRuler, 100);
					e.Graphics.DrawString(text, font, b, point.X, point.Y);
				}

				// performance
				if (Core.ShowPerformanceDiagnostic.Value)
				{
					int pos = point.Y + size.Height + 5;


					string VertexString = null;
					ulong VertexCounter = 0;
					lock (mGrbl3DLock)
					{VertexCounter = mGrbl3D != null ? mGrbl3D.VertexCounter : 0;}

					if (VertexCounter < 1000)
						VertexString = string.Format("{0,6:##0}", VertexCounter);
					else if (VertexCounter < 1000000)
						VertexString = string.Format("{0,6:###0.0} K", VertexCounter / 1000.0);
					else if (VertexCounter < 1000000000)
						VertexString = string.Format("{0,6:###0.0} M", VertexCounter / 1000000.0);
					else
						VertexString = string.Format("{0,6:###0.0} B", VertexCounter / 1000000000.0);

					text = $"VER   {VertexString}\nTIM   {string.Format("{0,6:##0} ms", RenderTime.Avg)}\nFPS   {string.Format("{0,6:##0}", 1000.0 / RefreshRate.Avg)}";
					size = MeasureText(text, font);
					point = new Point(Width - size.Width - mPadding.Right, pos);
					DrawOverlay(e, point, size, ColorScheme.PreviewRuler, 100);
					e.Graphics.DrawString(text, font, b, point.X, point.Y);
				}

				// loading
				lock (mGrbl3DLock)
				{
					if (mGrbl3D != null && mGrbl3D.LoadingPercentage < 100 || Core.ShowLaserOffMovements.Value && mGrbl3DOff != null && mGrbl3DOff.LoadingPercentage < 100)
					{
						int pos = point.Y + size.Height + 5;
						double? perc;
						perc = mGrbl3D?.LoadingPercentage ?? 0;
						perc += mGrbl3DOff?.LoadingPercentage ?? 0;
						if (Core.ShowLaserOffMovements.Value) perc /= 2;
						text = $"Loading {perc:0.0}%";
						size = MeasureText(text, font);
						point = new Point(Width - size.Width - mPadding.Right, pos);
						DrawOverlay(e, point, size, ColorScheme.PreviewRuler, 100);
						e.Graphics.DrawString(text, font, b, point.X, point.Y);
					}
				}
			}
		}

		private void DrawOverlay(PaintEventArgs e, Point point, Size size, Color color, int alfa)
		{
			Color c = Color.FromArgb(alfa, color);
			using (Brush brush = new SolidBrush(c))
			using (GraphicsPath path = Tools.Graph.RoundedRect(new Rectangle(point.X - 5, point.Y - 5, size.Width, size.Height), 7))
			{
				e.Graphics.FillPath(brush, path);
			}
		}

		private Size MeasureText(string text, Font font)
		{
			Size size = TextRenderer.MeasureText(text, font);
			size.Width += 10;
			size.Height += 5;
			return size;
		}

		private void GrblSceneControl_MouseLeave(object sender, EventArgs e)
		{
			mLastMousePos = null;
			mMouseWorldPosition = null;
		}

		private void GrblSceneControl_MouseUp(object sender, MouseEventArgs e)
		{
			Cursor.Current = Cursors.Default;
			mLastMousePos = null;
			mMouseWorldPosition = null;
		}

		private void GrblSceneControl_MouseDown(object sender, MouseEventArgs e)
		{
			Cursor.Current = Cursors.Cross;
			mLastMousePos = e.Location;
		}

		private void GrblSceneControl_MouseMove(object sender, MouseEventArgs e)
		{
			// compute mouse coord
			double xp = e.X / (double)Width * (mCamera.Right - mCamera.Left) + mCamera.Left;
			double yp = (Height - e.Y) / (double)Height * (mCamera.Top - mCamera.Bottom) + mCamera.Bottom;
			mMouseWorldPosition = new PointF((float)xp, (float)yp);
			// if in drag
			if (mLastMousePos != null && e.Button == MouseButtons.Left)
			{
				Cursor.Current = Cursors.SizeAll;
				Point lastPos = (Point)mLastMousePos;
				double k = (mCamera.Right - mCamera.Left) / Width;
				double dx = e.X - lastPos.X;
				double dy = e.Y - lastPos.Y;
				SetWorldPosition(mCamera.Left - dx * k, mCamera.Right - dx * k, mCamera.Bottom + dy * k, mCamera.Top + dy * k);
				mLastMousePos = e.Location;
			}
		}

		private void GrblSceneControl_MouseWheel(object sender, MouseEventArgs e)
		{
			if (mMouseWorldPosition != null)
			{
				float k = e.Delta / 1000f;
				PointF mousePos = (PointF)mMouseWorldPosition;
				SetWorldPosition(
					mCamera.Left + (mousePos.X - mCamera.Left) * k,
					mCamera.Right - (mCamera.Right - mousePos.X) * k,
					mCamera.Bottom + (mousePos.Y - mCamera.Bottom) * k,
					mCamera.Top - (mCamera.Top - mousePos.Y) * k
				);
			}
		}

		public void SetCore(GrblCore core)
		{
			Core = core;
			Core.OnFileLoaded += OnFileLoaded;
			Core.ShowExecutedCommands.OnChange += ShowExecutedCommands_OnChange;
			Core.PreviewLineSize.OnChange += PrerviewLineSize_OnChange;
			Core.OnAutoSizeDrawing += Core_OnAutoSizeDrawing;
			Core.OnProgramEnded += OnProgramEnded;
		}

		private void OnProgramEnded()
		{
			foreach (var command in Core.LoadedFile.Commands)
			{
				command.ClearResult();
			}
		}

		private void Core_OnAutoSizeDrawing(GrblCore obj)
		{
			AutoSizeDrawing();
		}

		private void PrerviewLineSize_OnChange(Tools.RetainedSetting<float> obj)
		{
			if (mGrbl3D != null) mGrbl3D.LineWidth = obj.Value;
			mInvalidateAll = true;
		}

		private void ShowExecutedCommands_OnChange(Tools.RetainedSetting<bool> obj) => mInvalidateAll = true;

		private void DisposeGrbl3D()
		{
			mReload = true;
		}

		private void OnFileLoaded(long elapsed, string filename)
		{
			DisposeGrbl3D();
			mReload = true;
			AutoSizeDrawing();
		}

		public void AutoSizeDrawing()
		{

			double ratio = Width / (double)Height;
			double availableWidth = Width - mPadding.Left - mPadding.Right;
			double availableHeight = Height - mPadding.Bottom - mPadding.Top;
			const double growFactor = 1.1;
			const decimal defaultViewport = 100;

			XYRange drawingRange;

			if (Core?.LoadedFile.Range.DrawingRange.ValidRange == true)
			{
				drawingRange = Core.LoadedFile.Range.DrawingRange;
			}
			else
			{
				drawingRange = new XYRange();
				drawingRange.X.Min = -defaultViewport;
				drawingRange.X.Max = defaultViewport;
				drawingRange.Y.Min = -defaultViewport;
				drawingRange.Y.Max = defaultViewport;
			}

			double drawingWidth = (double)drawingRange.Width * growFactor;
			double drawingHeight = (double)drawingRange.Height * growFactor;

			double widthRatio = drawingWidth / availableWidth;
			double paddingLeftWorld = mPadding.Left * widthRatio;
			double paddingRightWorld = mPadding.Right * widthRatio;

			double heightRatio = drawingHeight / availableHeight;
			double paddingBottomWorld = mPadding.Bottom * heightRatio;
			double paddingTopWorld = mPadding.Top * heightRatio;

			double left;
			double right;
			double bottom;
			double top;

			if (drawingWidth / drawingHeight > availableWidth / availableHeight)
			{
				left = (double)drawingRange.Center.X - drawingWidth / 2;
				right = (double)drawingRange.Center.X + drawingWidth / 2;

				left -= paddingLeftWorld;
				right += paddingRightWorld;

				double resizedHeight = (right - left) / ratio;
				double centerY = drawingRange.Center.Y;

				bottom = centerY - resizedHeight / 2;
				top = centerY + resizedHeight / 2;

				double bottomShift = (top - bottom) / Height * mPadding.Bottom / 2;
				bottom -= bottomShift;
				top -= bottomShift;
			}
			else
			{
				bottom = (double)drawingRange.Center.Y - drawingHeight / 2;
				top = (double)drawingRange.Center.Y + drawingHeight / 2;

				bottom -= paddingBottomWorld;
				top += paddingTopWorld;

				double resizedWidth = (top - bottom) * ratio;
				double centerX = drawingRange.Center.X;

				left = centerX - resizedWidth / 2;
				right = centerX + resizedWidth / 2;

				double leftShift = (right - left) / Width * mPadding.Left / 2;
				left -= leftShift;
				right -= leftShift;
			}

			SetWorldPosition(
				left,
				right,
				bottom,
				top
			);
		}

		public void TimerUpdate()
		{
			if (Core != null && (mLastWPos != Core.WorkPosition || mLastMPos != Core.MachinePosition || mCurF != Core.CurrentF || mCurS != Core.CurrentS))
			{
				mLastWPos = Core.WorkPosition;
				mLastMPos = Core.MachinePosition;
				mCurF = Core.CurrentF;
				mCurS = Core.CurrentS;
			}
			Invalidate();
		}

		public void OnColorChange()
		{
			mBackgroundColor = ColorScheme.PreviewBackColor;
			mTextColor = ColorScheme.PreviewText;
			mOriginsColor = ColorScheme.PreviewRuler;
			mPointerColor = ColorScheme.PreviewCross;
			mTicksColor = ColorScheme.PreviewGrid;
			mMinorsColor = ColorScheme.PreviewGridMinor;
			mInvalidateAll = true;
		}

		private void TimIssueDetector_Tick(object sender, EventArgs e)
		{
			TimIssueDetector.Enabled = false;

			if (Width > 0 && Height > 0)
			{ 
				Bitmap bitmap = new Bitmap(Width, Height);
				// Draw the control to the bitmap
				DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));

				Color c = bitmap.GetPixel(1, 1);

				if (c.Equals(IssueMatchBackColor))
					MessageBox.Show("LaserGRBL is unable to draw the preview, it seems your video card is not supported. Try starting LaserGRBL again from the start menu by choosing the \"LaserGRBL (soft opengl)\" option.");
			}

		}

		private void GrblPanel3D_Load(object sender, EventArgs e)
		{
			this.Load -= GrblPanel3D_Load;
			//TimIssueDetector.Start(); //uncomment to enable issue detector using bitmap pixel color test
		}
	}

}