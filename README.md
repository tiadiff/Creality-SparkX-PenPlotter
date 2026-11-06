# SparkX Pen Plotter Studio

**SparkX Pen Plotter Studio** is a standalone, high-performance desktop application built in C# (.NET 8 WPF) designed to turn the **Creality SparkX i7** (and other Klipper-based 3D printers) into a safe, precision 2D pen plotter.

## Key Features

- **Universal Image & Vector Input**:
  - Direct native parsing of vector `.svg` files (including cubic/quadratic Bezier curves, lines, rects, polylines, polygons).
  - Built-in raster image vectorizer for `.png`, `.jpg`, `.jpeg`, `.bmp` files (Sobel edge detection, contour tracer, and Ramer-Douglas-Peucker simplification).
- **Marker & Pen Profiles**:
  - **Felt-tip Marker mode**: Merges dense lines, suppresses micro-dots, and prevents ink soaking and bleeding into the paper.
  - **Fine Pen / Ballpoint mode**: Fine resolution for technical diagrams and handwriting.
  - **Artistic / Detailed mode**: High contour density for sketching.
- **Safety First & Hardware Protection**:
  - Enforces safety margins ($\ge 10\text{ mm}$) from paper edges on the print bed.
  - Heaters and part cooling fans are kept completely disabled (`M104 S0`, `M140 S0`, `M106 S0`).
  - Automatic filament runout sensor bypass via Klipper configuration and commands.
  - Critical pen-lifting sequence: lifts $Z$ at the start position before any horizontal $X/Y$ travel, preventing sheet scratching.
- **Interactive Live Preview**:
  - Scaled visualization of the print bed ($260 \times 260\text{ mm}$), centered paper sheet ($200 \times 150\text{ mm}$ or custom), and safety boundaries.
  - Differentiates drawing strokes (blue) from travel moves (red dashed lines).
- **Direct Network Plotting (Moonraker / Klipper API)**:
  - 1-Click "Dry-Run in Air" (test run at $Z + 5\text{ mm}$ with zero bed contact).
  - 1-Click "Draw on Paper" (uploads G-code and triggers real plot over Wi-Fi/Ethernet).
  - Real-time status monitoring (progress bar, toolhead coordinates, pause, cancel).
- **Standalone G-Code Export**:
  - Export clean G-Code files directly to USB drive or SD card.

## System Requirements

- Windows 10 / Windows 11 (x64)
- .NET 8.0 Runtime (Desktop App)

## Architecture

- **`Models/PlotterModels.cs`**: Data structures for points, vector paths, plotter settings, and printer status.
- **`Services/SvgParser.cs`**: High-speed, zero-dependency XML and SVG path tokenizer and Bezier interpolator.
- **`Services/ImageVectorizer.cs`**: Grayscale conversion, box smoothing, Sobel edge magnitude, contour tracing, and RDP polyline simplification.
- **`Services/GCodeGenerator.cs`**: Greedy nearest-neighbor travel optimizer, bed coordinate transformer, and safe plotter G-code generator.
- **`Services/MoonrakerClient.cs`**: Asynchronous HTTP client interfacing Klipper's Moonraker API for upload, print control, and telemetry.
- **`MainWindow.xaml` / `MainWindow.xaml.cs`**: Modern dark-themed WPF desktop GUI.

## Building from Source

To compile the application in Release mode:

```powershell
dotnet build -c Release
```

The resulting binary will be located at:
`bin\Release\net8.0-windows\SparkXPenPlotter.exe`
