using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SparkXPenPlotter.Models;
using SparkXPenPlotter.Services;

namespace SparkXPenPlotter;

public partial class MainWindow : Window
{
    private readonly PlotterSettings _settings = new();
    private readonly MoonrakerClient _client = new();
    private readonly DispatcherTimer _pollTimer = new();

    private List<VectorPath> _rawPaths = new();
    private List<VectorPath> _transformedPaths = new();
    private string? _currentFilePath;
    private bool _isUpdatingUi = false;
    private PrinterHardwareMode _currentHardwareMode = PrinterHardwareMode.Unknown;
    private int _hardwareCheckCounter = 0;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Interval = TimeSpan.FromSeconds(1.5);
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        // Load calibration sample by default if present
        string samplePath = @"c:\Users\matti\source\repos\blank0\sample_calibration.svg";
        if (File.Exists(samplePath))
        {
            LoadFile(samplePath);
        }
        else
        {
            UpdateCanvasPreview();
        }

        _ = CheckConnectionAsync();
    }

    private async void PollTimer_Tick(object? sender, EventArgs e)
    {
        await UpdatePrinterStatusAsync();
    }

    private async Task CheckConnectionAsync()
    {
        var status = await _client.GetStatusAsync(_settings.PrinterIp, _settings.PrinterPort);
        if (status.IsConnected)
        {
            _currentHardwareMode = await _client.CheckHardwareModeAsync(_settings.PrinterIp, _settings.PrinterPort);
            status.HardwareMode = _currentHardwareMode;
        }
        UpdateStatusUi(status);
    }

    private async Task UpdatePrinterStatusAsync()
    {
        string ip = TxtPrinterIp.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;

        var status = await _client.GetStatusAsync(ip, _settings.PrinterPort);
        if (status.IsConnected)
        {
            _hardwareCheckCounter++;
            if (_hardwareCheckCounter % 6 == 0 || _currentHardwareMode == PrinterHardwareMode.Unknown)
            {
                _currentHardwareMode = await _client.CheckHardwareModeAsync(ip, _settings.PrinterPort);
            }
            status.HardwareMode = _currentHardwareMode;
        }
        else
        {
            _currentHardwareMode = PrinterHardwareMode.Unknown;
            status.HardwareMode = PrinterHardwareMode.Unknown;
        }
        UpdateStatusUi(status);
    }

    private void UpdateStatusUi(PrinterStateInfo status)
    {
        if (status.IsConnected)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Green
            StatusText.Text = $"Connesso ({status.State})";
            TxtPrintState.Text = $"Stato: {status.State}";
            TxtToolheadPos.Text = $"Posizione: X={status.X:F1} Y={status.Y:F1} Z={status.Z:F1}";
            PrintProgress.Value = status.Progress;
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
            StatusText.Text = "Disconnesso";
            TxtPrintState.Text = "Stato: Offline";
            TxtToolheadPos.Text = "Posizione: X=-- Y=-- Z=--";
            PrintProgress.Value = 0;
        }

        // Update Hardware Mode Badge & Description
        if (BadgeMode != null && TxtModeBadge != null && TxtModeDescription != null)
        {
            if (status.HardwareMode == PrinterHardwareMode.PenPlotter)
            {
                BadgeMode.Background = new SolidColorBrush(Color.FromRgb(79, 70, 229)); // Indigo
                TxtModeBadge.Text = "✒️ Plotter (Protetto)";
                TxtModeBadge.Foreground = new SolidColorBrush(Color.FromRgb(224, 231, 255));
                TxtModeDescription.Text = "Home Z impostato su quota sicura (nessun tastatore su piatto). Sensore filamento disattivato. Pronto per disegnare con pennarello.";
            }
            else if (status.HardwareMode == PrinterHardwareMode.ThreeDPrinting)
            {
                BadgeMode.Background = new SolidColorBrush(Color.FromRgb(6, 95, 70)); // Emerald
                TxtModeBadge.Text = "🖨️ Stampa 3D (Fabbrica)";
                TxtModeBadge.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
                TxtModeDescription.Text = "Tutti i sensori di fabbrica attivi. Home Z esegue il tastatore sul piatto e il sensore di filamento è abilitato.";
            }
            else
            {
                BadgeMode.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85)); // Slate
                TxtModeBadge.Text = status.IsConnected ? "Non rilevato" : "Disconnesso";
                TxtModeBadge.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                TxtModeDescription.Text = status.IsConnected 
                    ? "Configurazione personalizzata o non standard. Usa i pulsanti per applicare un preset." 
                    : "Collega la stampante per verificare la modalità hardware corrente.";
            }
        }
    }

    #region File Loading & Processing

    private void DropBorder_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                LoadFile(files[0]);
            }
        }
    }

    private void DropBorder_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void DropBorder_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "File Supportati (*.svg;*.png;*.jpg;*.jpeg;*.bmp)|*.svg;*.png;*.jpg;*.jpeg;*.bmp|Vettoriali SVG (*.svg)|*.svg|Immagini Raster (*.png;*.jpg;*.bmp)|*.png;*.jpg;*.bmp"
        };
        if (dlg.ShowDialog() == true)
        {
            LoadFile(dlg.FileName);
        }
    }

    private void LoadFile(string path)
    {
        _currentFilePath = path;
        TxtLoadedFileName.Text = System.IO.Path.GetFileName(path);
        ProcessCurrentFile();
    }

    private void ProcessCurrentFile()
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;

        ReadSettingsFromUi();

        string ext = System.IO.Path.GetExtension(_currentFilePath).ToLowerInvariant();
        if (ext == ".svg")
        {
            string svgText = File.ReadAllText(_currentFilePath);
            _rawPaths = SvgParser.ParseSvg(svgText);
        }
        else
        {
            int thresh = (int)SliderThreshold.Value;
            double minStroke = SliderMinStroke.Value;
            bool marker = CmbPreset.SelectedIndex == 0;
            _rawPaths = ImageVectorizer.VectorizeImage(_currentFilePath, thresh, minStroke, marker);
        }

        _transformedPaths = GCodeGenerator.TransformAndCenterPaths(_rawPaths, _settings);
        TxtStrokesInfo.Text = $"Tratti vettoriali: {_transformedPaths.Count}";

        UpdateCanvasPreview();
    }

    #endregion

    #region Canvas Visualization

    private void UpdateCanvasPreview()
    {
        PreviewCanvas.Children.Clear();
        ReadSettingsFromUi();

        double canvasW = PreviewCanvas.Width;
        double canvasH = PreviewCanvas.Height;

        double bedW = _settings.BedWidth;
        double bedH = _settings.BedHeight;
        double scale = canvasW / bedW;

        Point toCanvas(double x, double y) => new(x * scale, canvasH - (y * scale));

        // 1. Bed Grid
        for (double i = 0; i <= bedW; i += 20)
        {
            var p1 = toCanvas(i, 0);
            var p2 = toCanvas(i, bedH);
            PreviewCanvas.Children.Add(new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                StrokeThickness = 1
            });

            var h1 = toCanvas(0, i);
            var h2 = toCanvas(bedW, i);
            PreviewCanvas.Children.Add(new Line
            {
                X1 = h1.X, Y1 = h1.Y, X2 = h2.X, Y2 = h2.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                StrokeThickness = 1
            });
        }

        // 2. Paper Sheet
        double paperX0 = (bedW - _settings.PaperWidth) / 2.0;
        double paperY0 = (bedH - _settings.PaperHeight) / 2.0;
        var paperTopLeft = toCanvas(paperX0, paperY0 + _settings.PaperHeight);

        var paperRect = new System.Windows.Shapes.Rectangle
        {
            Width = _settings.PaperWidth * scale,
            Height = _settings.PaperHeight * scale,
            Fill = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
            Stroke = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            StrokeThickness = 1.5,
            RadiusX = 2, RadiusY = 2
        };
        Canvas.SetLeft(paperRect, paperTopLeft.X);
        Canvas.SetTop(paperRect, paperTopLeft.Y);
        PreviewCanvas.Children.Add(paperRect);

        // 3. Safety Margin Line (Dashed Green)
        double margin = _settings.SafetyMargin;
        var marginTopLeft = toCanvas(paperX0 + margin, paperY0 + _settings.PaperHeight - margin);
        var marginRect = new System.Windows.Shapes.Rectangle
        {
            Width = (_settings.PaperWidth - 2 * margin) * scale,
            Height = (_settings.PaperHeight - 2 * margin) * scale,
            Stroke = new SolidColorBrush(Color.FromRgb(134, 239, 172)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        };
        Canvas.SetLeft(marginRect, marginTopLeft.X);
        Canvas.SetTop(marginRect, marginTopLeft.Y);
        PreviewCanvas.Children.Add(marginRect);

        // 4. Center Start Point (Origin Indicator)
        var centerPt = toCanvas(bedW / 2.0, bedH / 2.0);
        var centerDot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129))
        };
        Canvas.SetLeft(centerDot, centerPt.X - 4);
        Canvas.SetTop(centerDot, centerPt.Y - 4);
        PreviewCanvas.Children.Add(centerDot);

        if (_transformedPaths == null || _transformedPaths.Count == 0) return;

        // 5. Travel moves (Red Dashed)
        var redBrush = new SolidColorBrush(Color.FromRgb(244, 63, 94));
        var prevPt = centerPt;
        foreach (var path in _transformedPaths)
        {
            if (path.Count == 0) continue;
            var start = toCanvas(path[0].X, path[0].Y);
            PreviewCanvas.Children.Add(new Line
            {
                X1 = prevPt.X, Y1 = prevPt.Y, X2 = start.X, Y2 = start.Y,
                Stroke = redBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 }
            });
            prevPt = toCanvas(path[^1].X, path[^1].Y);
        }

        // 6. Drawing Strokes (Blue Solid)
        var blueBrush = new SolidColorBrush(Color.FromRgb(2, 132, 199));
        double strokeThick = CmbPreset.SelectedIndex == 0 ? 2.5 : 1.2;

        foreach (var path in _transformedPaths)
        {
            if (path.Count < 2) continue;
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                var p0 = toCanvas(path[0].X, path[0].Y);
                ctx.BeginFigure(p0, false, false);
                for (int i = 1; i < path.Count; i++)
                {
                    ctx.LineTo(toCanvas(path[i].X, path[i].Y), true, false);
                }
            }
            PreviewCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geom,
                Stroke = blueBrush,
                StrokeThickness = strokeThick,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }

    #endregion

    #region Settings & Controls

    private void ReadSettingsFromUi()
    {
        if (_isUpdatingUi) return;

        _settings.PrinterIp = TxtPrinterIp?.Text.Trim() ?? "192.168.78.50";
        if (double.TryParse(TxtBedW?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double bw)) _settings.BedWidth = bw;
        if (double.TryParse(TxtBedH?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double bh)) _settings.BedHeight = bh;
        if (double.TryParse(TxtPaperW?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pw)) _settings.PaperWidth = pw;
        if (double.TryParse(TxtPaperH?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ph)) _settings.PaperHeight = ph;
        if (double.TryParse(TxtMargin?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double m)) _settings.SafetyMargin = Math.Max(5, m);

        if (double.TryParse(TxtZDraw?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zd)) _settings.ZDraw = zd;
        if (double.TryParse(TxtZTravel?.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zt)) _settings.ZTravel = zt;
        if (int.TryParse(TxtDrawSpeed?.Text, out int ds)) _settings.DrawSpeed = ds;
        if (int.TryParse(TxtTravelSpeed?.Text, out int ts)) _settings.TravelSpeed = ts;
    }

    private void Setting_TextChanged(object sender, TextChangedEventArgs e)
    {
        ProcessCurrentFile();
    }

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SliderThreshold == null || SliderMinStroke == null) return;
        _isUpdatingUi = true;

        if (CmbPreset.SelectedIndex == 0) // Pennarello
        {
            SliderThreshold.Value = 45;
            SliderMinStroke.Value = 4.0;
        }
        else if (CmbPreset.SelectedIndex == 1) // Penna fine
        {
            SliderThreshold.Value = 35;
            SliderMinStroke.Value = 2.0;
        }
        else // Dettagli
        {
            SliderThreshold.Value = 25;
            SliderMinStroke.Value = 1.0;
        }

        _isUpdatingUi = false;
        ProcessCurrentFile();
    }

    private void SliderThreshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtThresholdVal != null) TxtThresholdVal.Text = ((int)e.NewValue).ToString();
        ProcessCurrentFile();
    }

    private void SliderMinStroke_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtMinStrokeVal != null) TxtMinStrokeVal.Text = $"{e.NewValue:F1} mm";
        ProcessCurrentFile();
    }

    #endregion

    #region Action Buttons

    private async void BtnCheckConnection_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        await CheckConnectionAsync();
        MessageBox.Show(
            StatusText.Text.Contains("Connesso") 
                ? $"Connessione riuscita a {_settings.PrinterIp}!\nStato Klipper: {TxtPrintState.Text}\nModalità rilevata: {TxtModeBadge.Text}" 
                : $"Impossibile connettersi a {_settings.PrinterIp}:7125.\nVerifica che la stampante sia accesa e collegata al Wi-Fi.",
            "Stato Connessione", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void BtnSetPlotterMode_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        var confirm = MessageBox.Show(
            "Vuoi configurare la stampante in MODALITÀ PEN PLOTTER?\n\n" +
            "Modifiche applicate:\n" +
            "• Home Z protetto: disabilita il tastatore sul piatto per non piegare o spezzare il pennarello ed evitare l'errore 'System Error'.\n" +
            "• Sensore di filamento: disabilitato per permettere di tracciare disegni senza caricare filamento.\n" +
            "• Riavvio sicuro del firmware Klipper (~10-15s).\n\n" +
            "Procedere con la configurazione Plotter?",
            "Attiva Modalità Pen Plotter", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            BtnSetPlotterMode.IsEnabled = false;
            BtnSet3DMode.IsEnabled = false;
            TxtModeDescription.Text = "Applicazione configurazione Pen Plotter e riavvio Klipper in corso... attendere circa 15 secondi...";

            var (ok, msg) = await _client.SetPlotterModeAsync(_settings.PrinterIp, _settings.PrinterPort);
            if (ok)
            {
                MessageBox.Show(msg, "Modalità Plotter Attiva", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(msg, "Errore Configurazione", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await CheckConnectionAsync();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            BtnSetPlotterMode.IsEnabled = true;
            BtnSet3DMode.IsEnabled = true;
        }
    }

    private async void BtnSet3DMode_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        var confirm = MessageBox.Show(
            "Vuoi ripristinare la stampante in MODALITÀ STAMPA 3D (FABBRICA)?\n\n" +
            "Modifiche applicate:\n" +
            "• Routine Home Z originale di fabbrica riattivata (PRES_CHECK + _HOME_Z con tastatore piezoelettrico sul piatto).\n" +
            "• Sensore di fine filamento riattivato al 100% per rilevare il filamento PLA/PETG.\n" +
            "• Riavvio sicuro del firmware Klipper (~10-15s).\n\n" +
            "IMPORTANTE: assicurati che il PENNARELLO sia stato rimosso prima di eseguire qualsiasi Home Z sul piatto!\n\n" +
            "Procedere con il ripristino di fabbrica per la stampa 3D?",
            "Ripristina Stampa 3D", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            BtnSetPlotterMode.IsEnabled = false;
            BtnSet3DMode.IsEnabled = false;
            TxtModeDescription.Text = "Ripristino configurazione di fabbrica e riavvio Klipper in corso... attendere circa 15 secondi...";

            var (ok, msg) = await _client.Set3DPrintingModeAsync(_settings.PrinterIp, _settings.PrinterPort);
            if (ok)
            {
                MessageBox.Show(msg, "Modalità Stampa 3D Ripristinata", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(msg, "Errore Ripristino", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            await CheckConnectionAsync();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            BtnSetPlotterMode.IsEnabled = true;
            BtnSet3DMode.IsEnabled = true;
        }
    }

    private async void BtnPark_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        // Safe park: lift pen high to Z=25, move to X=10, Y=10
        await _client.SendGCodeAsync(_settings.PrinterIp, _settings.PrinterPort, "G90\nG1 Z25.0 F1000\nG1 X10 Y10 F3000");
    }

    private void BtnSaveGCode_Click(object sender, RoutedEventArgs e)
    {
        if (_transformedPaths == null || _transformedPaths.Count == 0)
        {
            MessageBox.Show("Nessun disegno da esportare. Carica prima un'immagine o un file SVG.", "Avviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReadSettingsFromUi();
        string gcode = GCodeGenerator.GenerateGCode(_transformedPaths, _settings, false);

        var dlg = new SaveFileDialog
        {
            Filter = "G-Code File (*.gcode)|*.gcode",
            FileName = "plotter_drawing.gcode"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, gcode);
            MessageBox.Show($"File G-Code salvato con successo:\n{dlg.FileName}", "Salvataggio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void BtnDryRun_Click(object sender, RoutedEventArgs e)
    {
        await SendPlotJobAsync(dryRun: true);
    }

    private async void BtnStartPrint_Click(object sender, RoutedEventArgs e)
    {
        await SendPlotJobAsync(dryRun: false);
    }

    private async Task SendPlotJobAsync(bool dryRun)
    {
        if (_transformedPaths == null || _transformedPaths.Count == 0)
        {
            MessageBox.Show("Nessun disegno da tracciare. Carica prima un file.", "Avviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ReadSettingsFromUi();
        string mode = dryRun ? "TEST IN ARIA (Dry-Run a Z+5mm)" : "DISEGNO REALE SU CARTA";
        var confirm = MessageBox.Show(
            $"Avviare il {mode} sulla stampante a {_settings.PrinterIp}?\n\nAssicurati che la punta del pennarello sia allineata al centro del foglio prima di procedere.",
            "Conferma Avvio", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        string gcode = GCodeGenerator.GenerateGCode(_transformedPaths, _settings, dryRun);
        string filename = dryRun ? "plotter_dryrun.gcode" : "plotter_draw.gcode";

        var (ok, msg) = await _client.UploadAndPrintAsync(_settings.PrinterIp, _settings.PrinterPort, filename, gcode, true);
        if (ok)
        {
            MessageBox.Show($"Lavoro inviato con successo!\nLa stampante sta avviando il {mode}.\n\n{msg}", "Avviato", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Impossibile avviare il disegno sulla stampante:\n\n{msg}\n\nVerifica che la stampante non sia già occupata o in errore.", "Errore Avvio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUi();
        await _client.PauseAsync(_settings.PrinterIp, _settings.PrinterPort);
    }

    private async void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show("Sei sicuro di voler interrompere il disegno?", "Conferma Annullamento", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.Yes)
        {
            ReadSettingsFromUi();
            await _client.CancelAsync(_settings.PrinterIp, _settings.PrinterPort);
        }
    }

    #endregion
}
