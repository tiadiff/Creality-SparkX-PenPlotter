using System.Text;
using SparkXPenPlotter.Models;

namespace SparkXPenPlotter.Services;

public static class GCodeGenerator
{
    public static List<VectorPath> TransformAndCenterPaths(
        List<VectorPath> paths, 
        PlotterSettings settings)
    {
        if (paths == null || paths.Count == 0) return new List<VectorPath>();

        // 1. Calculate bounding box of input paths
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        foreach (var p in paths)
        {
            foreach (var pt in p)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        double origW = maxX - minX;
        double origH = maxY - minY;
        if (origW <= 0 || origH <= 0) return new List<VectorPath>();

        // 2. Safe drawable area inside paper
        double maxDrawW = settings.PaperWidth - 2 * settings.SafetyMargin;
        double maxDrawH = settings.PaperHeight - 2 * settings.SafetyMargin;

        if (maxDrawW <= 10) maxDrawW = 10;
        if (maxDrawH <= 10) maxDrawH = 10;

        double scale = Math.Min(maxDrawW / origW, maxDrawH / origH);

        double bedCx = settings.BedWidth / 2.0;
        double bedCy = settings.BedHeight / 2.0;

        double origCx = (minX + maxX) / 2.0;
        double origCy = (minY + maxY) / 2.0;

        var transformed = new List<VectorPath>();
        foreach (var path in paths)
        {
            var newPath = new VectorPath();
            foreach (var pt in path)
            {
                double tx = bedCx + (pt.X - origCx) * scale;
                // Invert Y: SVG/screen Y is downwards, 3D printer Y is upwards
                double ty = bedCy - (pt.Y - origCy) * scale;
                newPath.Add(new Point2D(Math.Round(tx, 2), Math.Round(ty, 2)));
            }
            transformed.Add(newPath);
        }

        return OptimizeStrokeOrder(transformed);
    }

    public static List<VectorPath> OptimizeStrokeOrder(List<VectorPath> paths)
    {
        if (paths.Count <= 1) return paths;

        var remaining = paths.Select(p => new VectorPath(p)).ToList();
        var ordered = new List<VectorPath>();
        var currentPos = new Point2D(0, 0);

        while (remaining.Count > 0)
        {
            int bestIdx = 0;
            double bestDist = double.MaxValue;
            bool reverseBest = false;

            for (int i = 0; i < remaining.Count; i++)
            {
                var p = remaining[i];
                double dStart = p[0].DistanceTo(currentPos);
                double dEnd = p[^1].DistanceTo(currentPos);

                if (dStart < bestDist)
                {
                    bestDist = dStart;
                    bestIdx = i;
                    reverseBest = false;
                }
                if (dEnd < bestDist)
                {
                    bestDist = dEnd;
                    bestIdx = i;
                    reverseBest = true;
                }
            }

            var chosen = remaining[bestIdx];
            remaining.RemoveAt(bestIdx);
            if (reverseBest) chosen.Reverse();

            ordered.Add(chosen);
            currentPos = chosen[^1];
        }

        return ordered;
    }

    public static string GenerateGCode(List<VectorPath> transformedPaths, PlotterSettings settings, bool dryRun = false)
    {
        var sb = new StringBuilder();

        double actualZDraw = dryRun ? settings.ZDraw + 5.0 : settings.ZDraw;
        double actualZTravel = dryRun ? settings.ZTravel + 5.0 : settings.ZTravel;

        sb.AppendLine("; ==================================================================");
        sb.AppendLine("; SparkX Pen Plotter Studio - G-Code Generator");
        sb.AppendLine(dryRun ? "; *** DRY-RUN MODE: Drawing in air (+5mm above paper) ***" : "; *** REAL PLOTTING MODE ***");
        sb.AppendLine($"; Bed Size: {settings.BedWidth:F1}x{settings.BedHeight:F1} mm | Paper: {settings.PaperWidth:F1}x{settings.PaperHeight:F1} mm");
        sb.AppendLine($"; Draw Z: {actualZDraw:F2} mm | Travel Z: {actualZTravel:F2} mm");
        sb.AppendLine("; ==================================================================\n");

        // Safety header
        sb.AppendLine("; --- SAFETY INITIALIZATION ---");
        sb.AppendLine("SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=0 ; Disable filament runout");
        sb.AppendLine("M104 S0 ; Extruder heater OFF");
        sb.AppendLine("M140 S0 ; Bed heater OFF");
        sb.AppendLine("M106 S0 ; Fan OFF");
        sb.AppendLine("G90     ; Absolute Positioning");
        sb.AppendLine("G21     ; Millimeter units\n");

        // Touch-off calibration and immediate lift
        sb.AppendLine("; --- CRITICAL: LIFT PEN BEFORE ANY HORIZONTAL MOVE ---");
        sb.AppendLine($"G92 Z{settings.ZDraw:F2} ; Register current position as contact height ({settings.ZDraw:F2}mm)");
        sb.AppendLine($"G1 Z{actualZTravel:F2} F800 ; LIFT PEN IMMEDIATELY!");
        sb.AppendLine("G4 P500 ; Dwell 0.5s to ensure pen is safely in the air\n");

        // Draw strokes
        sb.AppendLine("; --- BEGIN DRAWING STROKES ---");
        int total = transformedPaths.Count;

        for (int i = 0; i < total; i++)
        {
            var path = transformedPaths[i];
            if (path.Count < 2) continue;

            sb.AppendLine($"; Stroke {i + 1}/{total}");
            // Rapid travel above start
            sb.AppendLine(FormattableString.Invariant($"G1 X{path[0].X:F2} Y{path[0].Y:F2} F{settings.TravelSpeed}"));
            // Lower pen
            sb.AppendLine(FormattableString.Invariant($"G1 Z{actualZDraw:F2} F600 ; Pen Down"));

            // Trace path
            for (int j = 1; j < path.Count; j++)
            {
                sb.AppendLine(FormattableString.Invariant($"G1 X{path[j].X:F2} Y{path[j].Y:F2} F{settings.DrawSpeed}"));
            }

            // Lift pen
            sb.AppendLine(FormattableString.Invariant($"G1 Z{actualZTravel:F2} F800 ; Pen Up\n"));
        }

        // End sequence
        sb.AppendLine("; --- FINISH & PRESENT DRAWING ---");
        sb.AppendLine(FormattableString.Invariant($"G1 Z{actualZTravel + 15.0:F2} F1000 ; Lift pen high away from sheet"));
        sb.AppendLine(FormattableString.Invariant($"G1 X10 Y{settings.BedHeight - 20:F2} F{settings.TravelSpeed} ; Slide bed forward"));
        sb.AppendLine("M84 ; Disable steppers");
        sb.AppendLine("; === END OF PLOT ===");

        return sb.ToString();
    }
}
