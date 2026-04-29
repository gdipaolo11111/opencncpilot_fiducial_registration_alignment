using Microsoft.Win32;
using OpenCNCPilot.Communication;
using OpenCNCPilot.GCode;
using OpenCNCPilot.Util;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AForge.Video;
using AForge.Video.DirectShow;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace OpenCNCPilot
{
	public partial class MainWindow : Window, INotifyPropertyChanged
	{
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        // === PEGA AQUÍ LO NUEVO (PASO 1 de la lógica) ===
        private double cameraOffsetX = 0;
        private double cameraOffsetY = 0;

        private double p1MachineX = 0, p1MachineY = 0, p1GCodeX = 0, p1GCodeY = 0;
        private double p2MachineX = 0, p2MachineY = 0, p2GCodeX = 0, p2GCodeY = 0;
        private bool isP1Set = false;
        private bool isP2Set = false;
        // ===============================================
        Machine machine = new Machine();

		OpenFileDialog openFileDialogGCode = new OpenFileDialog() { Filter = Constants.FileFilterGCode };
		SaveFileDialog saveFileDialogGCode = new SaveFileDialog() { Filter = Constants.FileFilterGCode };
		OpenFileDialog openFileDialogHeightMap = new OpenFileDialog() { Filter = Constants.FileFilterHeightMap };
		SaveFileDialog saveFileDialogHeightMap = new SaveFileDialog() { Filter = Constants.FileFilterHeightMap };

		GCodeFile ToolPath { get; set; } = GCodeFile.Empty;
		HeightMap Map { get; set; }

		bool HeightMapApplied { get; set; } = false;

		GrblSettingsWindow settingsWindow = new GrblSettingsWindow();

		public event PropertyChangedEventHandler PropertyChanged;

		private void RaisePropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		public MainWindow()
		{
			AppDomain.CurrentDomain.UnhandledException += UnhandledException;

			InitializeComponent();

			// --- INICIO: Buscar cámaras para la alineación ---
			videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
				foreach (FilterInfo device in videoDevices)
				{
					ComboBoxCameras.Items.Add(device.Name);
				}
				if (ComboBoxCameras.Items.Count > 0)
				{
					ComboBoxCameras.SelectedIndex = 0; // Seleccionar la primera cámara por defecto
				}
            // --- FIN: Buscar cámaras ---

            // --- INICIO: Apagar cámara al cerrar la ventana ---
            this.Closing += (sender, e) =>
            {
                if (videoSource != null && videoSource.IsRunning)
                {
                    videoSource.SignalToStop();
                    videoSource.WaitForStop();
                }
            };
            // --- FIN: Apagar cámara al cerrar la ventana ---
            openFileDialogGCode.FileOk += OpenFileDialogGCode_FileOk;
			saveFileDialogGCode.FileOk += SaveFileDialogGCode_FileOk;
			openFileDialogHeightMap.FileOk += OpenFileDialogHeightMap_FileOk;
			saveFileDialogHeightMap.FileOk += SaveFileDialogHeightMap_FileOk;

			machine.ConnectionStateChanged += Machine_ConnectionStateChanged;

			machine.NonFatalException += Machine_NonFatalException;
			machine.Info += Machine_Info;
			machine.LineReceived += Machine_LineReceived;
			machine.LineReceived += settingsWindow.LineReceived;
			machine.StatusReceived += Machine_StatusReceived;
			machine.LineSent += Machine_LineSent;

			machine.PositionUpdateReceived += Machine_PositionUpdateReceived;
			machine.StatusChanged += Machine_StatusChanged;
			machine.DistanceModeChanged += Machine_DistanceModeChanged;
			machine.UnitChanged += Machine_UnitChanged;
			machine.PlaneChanged += Machine_PlaneChanged;
			machine.BufferStateChanged += Machine_BufferStateChanged;
			machine.OperatingModeChanged += Machine_OperatingMode_Changed;
			machine.FileChanged += Machine_FileChanged;
			machine.FilePositionChanged += Machine_FilePositionChanged;
			machine.ProbeFinished += Machine_ProbeFinished;
			machine.OverrideChanged += Machine_OverrideChanged;
			machine.PinStateChanged += Machine_PinStateChanged;

			Machine_OperatingMode_Changed();
			Machine_PositionUpdateReceived();

			Properties.Settings.Default.SettingChanging += Default_SettingChanging;
			FileRuntimeTimer.Tick += FileRuntimeTimer_Tick;

			machine.ProbeFinished += Machine_ProbeFinished_UserOutput;

			LoadMacros();

			settingsWindow.SendLine += machine.SendLine;

			machine.Calculator.GetGCode += () => ToolPath;

			CheckBoxUseExpressions_Changed(null, null);
			ButtonRestoreViewport_Click(null, null);

			UpdateCheck.CheckForUpdate();

			if (App.Args.Length > 0)
			{
				if (File.Exists(App.Args[0]))
				{
					openFileDialogGCode.FileName = App.Args[0];
					OpenFileDialogGCode_FileOk(null, null);
				}
			}
		}

		public Vector3 LastProbePosMachine { get; set; }
		public Vector3 LastProbePosWork { get; set; }

		private void Machine_ProbeFinished_UserOutput(Vector3 position, bool success)
		{
			LastProbePosMachine = machine.LastProbePosMachine;
			LastProbePosWork = machine.LastProbePosWork;

			RaisePropertyChanged("LastProbePosMachine");
			RaisePropertyChanged("LastProbePosWork");
		}

		private void UnhandledException(object sender, UnhandledExceptionEventArgs ea)
		{
			Exception e = (Exception)ea.ExceptionObject;

			string info = "Unhandled Exception:\r\nMessage:\r\n";
			info += e.Message;
			info += "\r\nStackTrace:\r\n";
			info += e.StackTrace;
			info += "\r\nToString():\r\n";
			info += e.ToString();

			MessageBox.Show(info);
			Console.WriteLine(info);

			try
			{
				System.IO.File.WriteAllText("OpenCNCPilot_Crash_Log.txt", info);
			}
			catch { }

			Environment.Exit(1);
		}

		private void Default_SettingChanging(object sender, System.Configuration.SettingChangingEventArgs e)
		{
			if (e.SettingName.Equals("JogFeed") ||
				e.SettingName.Equals("JogDistance") ||
				e.SettingName.Equals("ProbeFeed") ||
				e.SettingName.Equals("ProbeSafeHeight") ||
				e.SettingName.Equals("ProbeMinimumHeight") ||
				e.SettingName.Equals("ProbeMaxDepth") ||
				e.SettingName.Equals("SplitSegmentLength") ||
				e.SettingName.Equals("ViewportArcSplit") ||
				e.SettingName.Equals("ArcToLineSegmentLength") ||
				e.SettingName.Equals("ProbeXAxisWeight") ||
				e.SettingName.Equals("ConsoleFadeTime"))
			{
				if (((double)e.NewValue) <= 0)
					e.Cancel = true;
			}

			if (e.SettingName.Equals("SerialPortBaud") ||
				e.SettingName.Equals("StatusPollInterval") ||
				e.SettingName.Equals("ControllerBufferSize"))
			{
				if (((int)e.NewValue) <= 0)
					e.Cancel = true;
			}
		}

		private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
		{
			System.Diagnostics.Process.Start(e.Uri.AbsoluteUri);
		}

		public string Version
		{
			get
			{
				var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
				return $"{version}";
			}
		}

		public string WindowTitle
		{
			get
			{
				if (CurrentFileName.Length < 1)
					return $"OpenCNCPilot v{Version} by martin2250";
				else
					return $"OpenCNCPilot v{Version} by martin2250 - {CurrentFileName}";
			}
		}

		private void Window_Drop(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

				if (files.Length > 0)
				{
					string file = files[0];

					if (file.EndsWith(".hmap"))
					{
						if (machine.Mode == Machine.OperatingMode.Probe || Map != null)
							return;

						OpenHeightMap(file);
					}
					else
					{
						if (machine.Mode == Machine.OperatingMode.SendFile)
							return;

						try
						{
							machine.SetFile(System.IO.File.ReadAllLines(file));
						}
						catch (Exception ex)
						{
							MessageBox.Show(ex.Message);
						}
					}
				}
			}
		}

		private void Window_DragEnter(object sender, DragEventArgs e)
		{
			if (e.Data.GetDataPresent(DataFormats.FileDrop))
			{
				string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

				if (files.Length > 0)
				{
					string file = files[0];

					if (file.EndsWith(".hmap"))
					{
						if (machine.Mode != Machine.OperatingMode.Probe && Map == null)
						{
							e.Effects = DragDropEffects.Copy;
							return;
						}
					}
					else
					{
						if (machine.Mode != Machine.OperatingMode.SendFile)
						{
							e.Effects = DragDropEffects.Copy;
							return;
						}
					}
				}
			}

			e.Effects = DragDropEffects.None;
		}

		private void viewport_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == System.Windows.Input.Key.Space)
			{
				machine.FeedHold();
				e.Handled = true;
			}
		}

		private void ButtonRapidOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Content as string)
			{
				case "100%":
					machine.SendControl(0x95);
					break;
				case "50%":
					machine.SendControl(0x96);
					break;
				case "25%":
					machine.SendControl(0x97);
					break;
			}
		}

		private void ButtonFeedOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Tag as string)
			{
				case "100%":
					machine.SendControl(0x90);
					break;
				case "+10%":
					machine.SendControl(0x91);
					break;
				case "-10%":
					machine.SendControl(0x92);
					break;
				case "+1%":
					machine.SendControl(0x93);
					break;
				case "-1%":
					machine.SendControl(0x94);
					break;
			}
		}

		private void ButtonSpindleOverride_Click(object sender, RoutedEventArgs e)
		{
			Button b = sender as Button;

			if (b == null)
				return;

			switch (b.Tag as string)
			{
				case "100%":
					machine.SendControl(0x99);
					break;
				case "+10%":
					machine.SendControl(0x9A);
					break;
				case "-10%":
					machine.SendControl(0x9B);
					break;
				case "+1%":
					machine.SendControl(0x9C);
					break;
				case "-1%":
					machine.SendControl(0x9D);
					break;
			}
		}

		private void ButtonResetViewport_Click(object sender, RoutedEventArgs e)
		{
			viewport.Camera.Position = new System.Windows.Media.Media3D.Point3D(50, -150, 250);
			viewport.Camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(-50, 150, -250);
			viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
		}

		private void ButtonLayFlatViewport_Click(object sender, RoutedEventArgs e)                  // deHarro, 2024-08-23
		{
			viewport.Camera.Position = new System.Windows.Media.Media3D.Point3D(0, 10, 250);
			viewport.Camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, -250);
			viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
		}

		private void ButtonRestoreViewport_Click(object sender, RoutedEventArgs e)
		{
			string[] scoords = Properties.Settings.Default.ViewPortPos.Split(';');

			try
			{
				IEnumerable<double> coords = scoords.Select(s => double.Parse(s));

				viewport.Camera.Position = new Vector3(coords.Take(3).ToArray()).ToPoint3D();
				viewport.Camera.LookDirection = new Vector3(coords.Skip(3).ToArray()).ToVector3D();
				viewport.Camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 0, 1);
			}
			catch
			{
				ButtonResetViewport_Click(null, null);
			}
		}

		private void ButtonSaveViewport_Click(object sender, RoutedEventArgs e)
		{
			List<double> coords = new List<double>();

			coords.AddRange(new Vector3(viewport.Camera.Position).Array);
			coords.AddRange(new Vector3(viewport.Camera.LookDirection).Array);

			Properties.Settings.Default.ViewPortPos = string.Join(";", coords.Select(d => d.ToString()));
		}

		private void ButtonSaveTLOPos_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			double Z = (Properties.Settings.Default.TLSUseActualPos) ? machine.MachinePosition.Z : LastProbePosMachine.Z;

			Properties.Settings.Default.ToolLengthSetterPos = Z;
		}

		private void ButtonApplyTLO_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			double Z = (Properties.Settings.Default.TLSUseActualPos) ? machine.MachinePosition.Z : LastProbePosMachine.Z;

			double delta = Z - Properties.Settings.Default.ToolLengthSetterPos;

			machine.SendLine($"G43.1 Z{delta.ToString(Constants.DecimalOutputFormat)}");
		}

		private void ButtonClearTLO_Click(object sender, RoutedEventArgs e)
		{
			if (machine.Mode != Machine.OperatingMode.Manual)
				return;

			machine.SendLine("G49");
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
			source.AddHook(WndProc);
		}

		private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (msg == App.WM_COPYDATA)
			{
				App.COPYDATASTRUCT _dataStruct = Marshal.PtrToStructure<App.COPYDATASTRUCT>(lParam);
				string _strMsg = Marshal.PtrToStringUni(_dataStruct.lpData, _dataStruct.cbData / 2);
				if (File.Exists(_strMsg))
				{
					Activate();
					openFileDialogGCode.FileName = _strMsg;
					OpenFileDialogGCode_FileOk(null, null);
				}
			}

			return IntPtr.Zero;
		}

        // ==========================================
        // ALINEACIÓN CON CÁMARA (Nuevas funciones)
        // ==========================================

        private void ButtonStartCamera_Click(object sender, RoutedEventArgs e)
        {
            if (videoDevices != null && videoDevices.Count > 0)
            {
                videoSource = new VideoCaptureDevice(videoDevices[ComboBoxCameras.SelectedIndex].MonikerString);
                videoSource.NewFrame += new NewFrameEventHandler(video_NewFrame);
                videoSource.Start();
            }
        }

        // Esta función se ejecuta por cada fotograma (frame) que manda la cámara
        private void video_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            try
            {
                Bitmap bitmap = (Bitmap)eventArgs.Frame.Clone();

                // Convertir la imagen de la cámara a un formato que WPF pueda mostrar
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                    memory.Position = 0;
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = memory;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze(); // Muy importante para que no dé error al pasarlo a la pantalla

                    // Enviar la imagen a nuestra interfaz
                    Dispatcher.BeginInvoke(new System.Action(delegate
                    {
                        CameraImage.Source = bitmapImage;
                    }));
                }
            }
            catch { }
        }

        private void ButtonSetOffset_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "Instrucciones para calibrar:\n\n" +
                "1. Haz una pequeña marca en el material con la fresa/broca.\n" +
                "2. Pon los ejes X e Y a cero (botón 'Zero (G10)' o 'Zero (G92)').\n" +
                "3. Mueve la máquina hasta que la cruz roja de la cámara esté exactamente sobre la marca.\n\n" +
                "¿Has hecho esto y quieres guardar el offset?",
                "Calibrar Offset de Cámara", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                // El offset será la posición actual de trabajo (lo que nos hemos movido desde el agujero)
                cameraOffsetX = machine.WorkPosition.X;
                cameraOffsetY = machine.WorkPosition.Y;

                MessageBox.Show($"Offset guardado correctamente:\nX = {cameraOffsetX:F3} mm\nY = {cameraOffsetY:F3} mm");
            }
        }
        private void ButtonSetP1_Click(object sender, RoutedEventArgs e)
        {
            if (cameraOffsetX == 0 && cameraOffsetY == 0)
            {
                MessageBox.Show("¡Cuidado! Aún no has configurado el Offset de la cámara.");
                return;
            }

            // Calculamos la posición real del husillo usando la cámara y el offset
            p1MachineX = machine.MachinePosition.X - cameraOffsetX;
            p1MachineY = machine.MachinePosition.Y - cameraOffsetY;

            // Pedimos al usuario las coordenadas teóricas de este punto en su G-Code
            OpenCNCPilot.EnterNumberWindow enwX = new OpenCNCPilot.EnterNumberWindow(0);
            enwX.Title = "Punto 1: Coordenada X teórica (G-Code)";
            enwX.ShowDialog();
            if (!enwX.Ok) return; // CORREGIDO AQUÍ
            p1GCodeX = enwX.Value; // CORREGIDO AQUÍ

            OpenCNCPilot.EnterNumberWindow enwY = new OpenCNCPilot.EnterNumberWindow(0);
            enwY.Title = "Punto 1: Coordenada Y teórica (G-Code)";
            enwY.ShowDialog();
            if (!enwY.Ok) return; // CORREGIDO AQUÍ
            p1GCodeY = enwY.Value; // CORREGIDO AQUÍ

            isP1Set = true;
            MessageBox.Show($"PUNTO 1 GUARDADO\n\nPosición Real Máquina: X={p1MachineX:F3}, Y={p1MachineY:F3}\nPosición G-Code: X={p1GCodeX:F3}, Y={p1GCodeY:F3}");
        }

        private void ButtonSetP2_Click(object sender, RoutedEventArgs e)
        {
            if (!isP1Set)
            {
                MessageBox.Show("Por favor, guarda primero el Punto 1.");
                return;
            }

            p2MachineX = machine.MachinePosition.X - cameraOffsetX;
            p2MachineY = machine.MachinePosition.Y - cameraOffsetY;

            OpenCNCPilot.EnterNumberWindow enwX = new OpenCNCPilot.EnterNumberWindow(0);
            enwX.Title = "Punto 2: Coordenada X teórica (G-Code)";
            enwX.ShowDialog();
            if (!enwX.Ok) return; // CORREGIDO AQUÍ
            p2GCodeX = enwX.Value; // CORREGIDO AQUÍ

            OpenCNCPilot.EnterNumberWindow enwY = new OpenCNCPilot.EnterNumberWindow(0);
            enwY.Title = "Punto 2: Coordenada Y teórica (G-Code)";
            enwY.ShowDialog();
            if (!enwY.Ok) return; // CORREGIDO AQUÍ
            p2GCodeY = enwY.Value; // CORREGIDO AQUÍ

            isP2Set = true;
            MessageBox.Show($"PUNTO 2 GUARDADO\n\nPosición Real Máquina: X={p2MachineX:F3}, Y={p2MachineY:F3}\nPosición G-Code: X={p2GCodeX:F3}, Y={p2GCodeY:F3}");
        }

        private void ButtonApplyAlignment_Click(object sender, RoutedEventArgs e)
		{
			// Aquí calcularemos la rotación y moveremos el G-Code
		}
	}
    }
