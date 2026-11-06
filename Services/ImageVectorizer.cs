using System.IO;
using System.Windows.Media.Imaging;
using SparkXPenPlotter.Models;

namespace SparkXPenPlotter.Services;

public static class ImageVectorizer
{
    public static List<VectorPath> VectorizeImage(string filePath, int threshold = 55, double minStrokeLengthMm = 4.0, bool markerMode = true)
    {
        // 1. Load image to 8bpp grayscale using native WPF BitmapSource
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        // Resize if too large (e.g. limit to 1400 px width for smooth vectors)
        int targetW = frame.PixelWidth;
        int targetH = frame.PixelHeight;
        if (targetW > 1400)
        {
            double scale = 1400.0 / targetW;
            targetW = 1400;
            targetH = (int)(frame.PixelHeight * scale);
        }

        var grayBitmap = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Gray8, null, 0);
        
        // Extract raw grayscale pixels
        int stride = grayBitmap.PixelWidth;
        byte[] pixels = new byte[stride * grayBitmap.PixelHeight];
        grayBitmap.CopyPixels(pixels, stride, 0);

        int width = grayBitmap.PixelWidth;
        int height = grayBitmap.PixelHeight;

        // 2. Simple Box / Gaussian Blur (3x3) to smooth out camera noise
        byte[] blurred = new byte[width * height];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int sum = 
                    pixels[(y - 1) * width + (x - 1)] + 2 * pixels[(y - 1) * width + x] + pixels[(y - 1) * width + (x + 1)] +
                    2 * pixels[y * width + (x - 1)]       + 4 * pixels[y * width + x]       + 2 * pixels[y * width + (x + 1)] +
                    pixels[(y + 1) * width + (x - 1)] + 2 * pixels[(y + 1) * width + x] + pixels[(y + 1) * width + (x + 1)];
                blurred[y * width + x] = (byte)(sum / 16);
            }
        }

        // 3. Sobel Edge Detection
        byte[] edges = new byte[width * height];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int gx = 
                    -blurred[(y - 1) * width + (x - 1)] + blurred[(y - 1) * width + (x + 1)] +
                    -2 * blurred[y * width + (x - 1)]   + 2 * blurred[y * width + (x + 1)] +
                    -blurred[(y + 1) * width + (x - 1)] + blurred[(y + 1) * width + (x + 1)];

                int gy = 
                    -blurred[(y - 1) * width + (x - 1)] - 2 * blurred[(y - 1) * width + x] - blurred[(y - 1) * width + (x + 1)] +
                    blurred[(y + 1) * width + (x - 1)]  + 2 * blurred[(y + 1) * width + x]  + blurred[(y + 1) * width + (x + 1)];

                int mag = (int)Math.Sqrt(gx * gx + gy * gy);
                edges[y * width + x] = (byte)(mag > threshold ? 255 : 0);
            }
        }

        // 4. Contour Tracking (Follow connected edge pixels into polylines)
        bool[] visited = new bool[width * height];
        var rawPaths = new List<VectorPath>();

        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
        int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

        for (int y = 2; y < height - 2; y++)
        {
            for (int x = 2; x < width - 2; x++)
            {
                int idx = y * width + x;
                if (edges[idx] == 255 && !visited[idx])
                {
                    // Start a new line trace
                    var path = new VectorPath();
                    int cx = x;
                    int cy = y;
                    path.Add(new Point2D(cx, cy));
                    visited[idx] = true;

                    bool foundNext;
                    do
                    {
                        foundNext = false;
                        for (int k = 0; k < 8; k++)
                        {
                            int nx = cx + dx[k];
                            int ny = cy + dy[k];
                            if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                            {
                                int nidx = ny * width + nx;
                                if (edges[nidx] == 255 && !visited[nidx])
                                {
                                    visited[nidx] = true;
                                    cx = nx;
                                    cy = ny;
                                    path.Add(new Point2D(cx, cy));
                                    foundNext = true;
                                    break;
                                }
                            }
                        }
                    } while (foundNext);

                    if (path.Count > 4)
                    {
                        rawPaths.Add(path);
                    }
                }
            }
        }

        // 5. Simplify paths using Ramer-Douglas-Peucker (RDP) algorithm
        double epsilon = markerMode ? 1.8 : 1.0;
        double minPixels = markerMode ? 35 : 15;

        var simplifiedPaths = new List<VectorPath>();
        foreach (var path in rawPaths)
        {
            if (path.TotalLength < minPixels) continue;

            var simplified = RamerDouglasPeucker(path, epsilon);
            if (simplified.Count >= 2)
            {
                simplifiedPaths.Add(simplified);
            }
        }

        return simplifiedPaths;
    }

    public static VectorPath RamerDouglasPeucker(List<Point2D> points, double epsilon)
    {
        if (points.Count < 3) return new VectorPath(points);

        double maxDist = 0;
        int maxIndex = 0;
        Point2D first = points[0];
        Point2D last = points[^1];

        for (int i = 1; i < points.Count - 1; i++)
        {
            double dist = PerpendicularDistance(points[i], first, last);
            if (dist > maxDist)
            {
                maxDist = dist;
                maxIndex = i;
            }
        }

        if (maxDist > epsilon)
        {
            var left = RamerDouglasPeucker(points.Take(maxIndex + 1).ToList(), epsilon);
            var right = RamerDouglasPeucker(points.Skip(maxIndex).ToList(), epsilon);
            var result = new VectorPath(left);
            result.AddRange(right.Skip(1));
            return result;
        }
        else
        {
            return new VectorPath { first, last };
        }
    }

    private static double PerpendicularDistance(Point2D pt, Point2D lineStart, Point2D lineEnd)
    {
        double dx = lineEnd.X - lineStart.X;
        double dy = lineEnd.Y - lineStart.Y;
        double mag = Math.Sqrt(dx * dx + dy * dy);
        if (mag < 1e-6) return pt.DistanceTo(lineStart);
        return Math.Abs(dy * pt.X - dx * pt.Y + lineEnd.X * lineStart.Y - lineEnd.Y * lineStart.X) / mag;
    }
}
