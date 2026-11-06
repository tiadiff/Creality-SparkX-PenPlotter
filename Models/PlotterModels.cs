namespace SparkXPenPlotter.Models;

public struct Point2D
{
    public double X { get; set; }
    public double Y { get; set; }

    public Point2D(double x, double y)
    {
        X = x;
        Y = y;
    }

    public readonly double DistanceTo(Point2D other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override readonly string ToString() => $"({X:F2}, {Y:F2})";
}

public class VectorPath : List<Point2D>
{
    public VectorPath() { }
    public VectorPath(IEnumerable<Point2D> pts) : base(pts) { }

    public double TotalLength
    {
        get
        {
            if (Count < 2) return 0;
            double len = 0;
            for (int i = 1; i < Count; i++)
            {
                len += this[i - 1].DistanceTo(this[i]);
            }
            return len;
        }
    }
}

public class PlotterSettings
{
    public double BedWidth { get; set; } = 260.0;
    public double BedHeight { get; set; } = 260.0;
    
    public double PaperWidth { get; set; } = 200.0;
    public double PaperHeight { get; set; } = 150.0;
    
    public double SafetyMargin { get; set; } = 15.0; // mm from paper edge
    
    public double ZDraw { get; set; } = 5.0;     // Contact with paper
    public double ZTravel { get; set; } = 8.0;   // In the air (Z-Hop)
    
    public int DrawSpeed { get; set; } = 1500;   // mm/min
    public int TravelSpeed { get; set; } = 3000; // mm/min
    
    public string PrinterIp { get; set; } = "192.168.78.50";
    public int PrinterPort { get; set; } = 7125;
}

public class PrinterStateInfo
{
    public bool IsConnected { get; set; }
    public string State { get; set; } = "Offline";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Progress { get; set; }
    public string Filename { get; set; } = "";
    public bool FilamentDetected { get; set; }
}
