using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SparkXPenPlotter.Models;

namespace SparkXPenPlotter.Services;

public static class SvgParser
{
    private static readonly Regex TokenRegex = new(@"([MmLlHhVvCcSsQqTtAaZz])|([-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?)", RegexOptions.Compiled);

    public static List<VectorPath> ParseSvg(string svgContent)
    {
        var paths = new List<VectorPath>();
        var doc = XDocument.Parse(svgContent);
        if (doc.Root == null) return paths;

        foreach (var el in doc.Descendants())
        {
            var localName = el.Name.LocalName.ToLowerInvariant();
            switch (localName)
            {
                case "path":
                    var d = el.Attribute("d")?.Value;
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        paths.AddRange(ParsePathData(d));
                    }
                    break;

                case "line":
                    if (TryParseDouble(el.Attribute("x1")?.Value, out double x1) &&
                        TryParseDouble(el.Attribute("y1")?.Value, out double y1) &&
                        TryParseDouble(el.Attribute("x2")?.Value, out double x2) &&
                        TryParseDouble(el.Attribute("y2")?.Value, out double y2))
                    {
                        paths.Add(new VectorPath { new(x1, y1), new(x2, y2) });
                    }
                    break;

                case "rect":
                    if (TryParseDouble(el.Attribute("x")?.Value, out double rx, 0) &&
                        TryParseDouble(el.Attribute("y")?.Value, out double ry, 0) &&
                        TryParseDouble(el.Attribute("width")?.Value, out double rw) &&
                        TryParseDouble(el.Attribute("height")?.Value, out double rh) && rw > 0 && rh > 0)
                    {
                        paths.Add(new VectorPath
                        {
                            new(rx, ry),
                            new(rx + rw, ry),
                            new(rx + rw, ry + rh),
                            new(rx, ry + rh),
                            new(rx, ry)
                        });
                    }
                    break;

                case "circle":
                    if (TryParseDouble(el.Attribute("cx")?.Value, out double cx, 0) &&
                        TryParseDouble(el.Attribute("cy")?.Value, out double cy, 0) &&
                        TryParseDouble(el.Attribute("r")?.Value, out double r) && r > 0)
                    {
                        var circlePath = new VectorPath();
                        for (int i = 0; i <= 36; i++)
                        {
                            double angle = 2 * Math.PI * i / 36;
                            circlePath.Add(new Point2D(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
                        }
                        paths.Add(circlePath);
                    }
                    break;

                case "polyline":
                case "polygon":
                    var ptsAttr = el.Attribute("points")?.Value;
                    if (!string.IsNullOrWhiteSpace(ptsAttr))
                    {
                        var matches = Regex.Matches(ptsAttr, @"[-+]?(?:\d*\.\d+|\d+)");
                        if (matches.Count >= 4)
                        {
                            var poly = new VectorPath();
                            for (int i = 0; i < matches.Count - 1; i += 2)
                            {
                                if (double.TryParse(matches[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double px) &&
                                    double.TryParse(matches[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double py))
                                {
                                    poly.Add(new Point2D(px, py));
                                }
                            }
                            if (localName == "polygon" && poly.Count > 0 && poly[0].DistanceTo(poly[^1]) > 0.001)
                            {
                                poly.Add(poly[0]);
                            }
                            if (poly.Count > 1) paths.Add(poly);
                        }
                    }
                    break;
            }
        }

        return paths.Where(p => p.Count > 1).ToList();
    }

    private static List<VectorPath> ParsePathData(string d)
    {
        var paths = new List<VectorPath>();
        var currentPath = new VectorPath();

        var matches = TokenRegex.Matches(d);
        var tokens = new List<object>();

        foreach (Match m in matches)
        {
            if (m.Groups[1].Success)
            {
                tokens.Add(m.Groups[1].Value[0]); // char command
            }
            else if (m.Groups[2].Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                tokens.Add(num);
            }
        }

        Point2D currentPt = new(0, 0);
        Point2D startSubpathPt = new(0, 0);
        Point2D? lastCubicCtrl = null;
        Point2D? lastQuadCtrl = null;

        char? cmd = null;
        int idx = 0;

        while (idx < tokens.Count)
        {
            if (tokens[idx] is char c)
            {
                cmd = c;
                idx++;
            }

            if (!cmd.HasValue) break;
            char command = cmd.Value;

            switch (command)
            {
                case 'M':
                case 'm':
                    if (idx + 1 >= tokens.Count) break;
                    double mx = (double)tokens[idx++];
                    double my = (double)tokens[idx++];
                    if (command == 'm') { mx += currentPt.X; my += currentPt.Y; }

                    if (currentPath.Count > 1) paths.Add(currentPath);
                    currentPath = new VectorPath { new(mx, my) };
                    currentPt = new(mx, my);
                    startSubpathPt = currentPt;
                    lastCubicCtrl = null;
                    lastQuadCtrl = null;
                    cmd = (command == 'm') ? 'l' : 'L';
                    break;

                case 'L':
                case 'l':
                    if (idx + 1 >= tokens.Count) break;
                    double lx = (double)tokens[idx++];
                    double ly = (double)tokens[idx++];
                    if (command == 'l') { lx += currentPt.X; ly += currentPt.Y; }
                    currentPt = new(lx, ly);
                    currentPath.Add(currentPt);
                    lastCubicCtrl = null;
                    lastQuadCtrl = null;
                    break;

                case 'H':
                case 'h':
                    if (idx >= tokens.Count) break;
                    double hx = (double)tokens[idx++];
                    if (command == 'h') hx += currentPt.X;
                    currentPt = new(hx, currentPt.Y);
                    currentPath.Add(currentPt);
                    lastCubicCtrl = null;
                    lastQuadCtrl = null;
                    break;

                case 'V':
                case 'v':
                    if (idx >= tokens.Count) break;
                    double vy = (double)tokens[idx++];
                    if (command == 'v') vy += currentPt.Y;
                    currentPt = new(currentPt.X, vy);
                    currentPath.Add(currentPt);
                    lastCubicCtrl = null;
                    lastQuadCtrl = null;
                    break;

                case 'C':
                case 'c':
                    if (idx + 5 >= tokens.Count) break;
                    double cx1 = (double)tokens[idx++]; double cy1 = (double)tokens[idx++];
                    double cx2 = (double)tokens[idx++]; double cy2 = (double)tokens[idx++];
                    double cex = (double)tokens[idx++]; double cey = (double)tokens[idx++];
                    if (command == 'c')
                    {
                        cx1 += currentPt.X; cy1 += currentPt.Y;
                        cx2 += currentPt.X; cy2 += currentPt.Y;
                        cex += currentPt.X; cey += currentPt.Y;
                    }
                    AddCubicBezier(currentPath, currentPt, new(cx1, cy1), new(cx2, cy2), new(cex, cey));
                    currentPt = new(cex, cey);
                    lastCubicCtrl = new(cx2, cy2);
                    lastQuadCtrl = null;
                    break;

                case 'S':
                case 's':
                    if (idx + 3 >= tokens.Count) break;
                    double sx2 = (double)tokens[idx++]; double sy2 = (double)tokens[idx++];
                    double sex = (double)tokens[idx++]; double sey = (double)tokens[idx++];
                    if (command == 's') { sx2 += currentPt.X; sy2 += currentPt.Y; sex += currentPt.X; sey += currentPt.Y; }
                    Point2D sctrl1 = lastCubicCtrl.HasValue
                        ? new(2 * currentPt.X - lastCubicCtrl.Value.X, 2 * currentPt.Y - lastCubicCtrl.Value.Y)
                        : currentPt;
                    AddCubicBezier(currentPath, currentPt, sctrl1, new(sx2, sy2), new(sex, sey));
                    currentPt = new(sex, sey);
                    lastCubicCtrl = new(sx2, sy2);
                    lastQuadCtrl = null;
                    break;

                case 'Q':
                case 'q':
                    if (idx + 3 >= tokens.Count) break;
                    double qx1 = (double)tokens[idx++]; double qy1 = (double)tokens[idx++];
                    double qex = (double)tokens[idx++]; double qey = (double)tokens[idx++];
                    if (command == 'q') { qx1 += currentPt.X; qy1 += currentPt.Y; qex += currentPt.X; qey += currentPt.Y; }
                    AddQuadBezier(currentPath, currentPt, new(qx1, qy1), new(qex, qey));
                    currentPt = new(qex, qey);
                    lastQuadCtrl = new(qx1, qy1);
                    lastCubicCtrl = null;
                    break;

                case 'Z':
                case 'z':
                    if (currentPt.DistanceTo(startSubpathPt) > 0.001)
                    {
                        currentPath.Add(startSubpathPt);
                        currentPt = startSubpathPt;
                    }
                    if (currentPath.Count > 1) paths.Add(currentPath);
                    currentPath = new VectorPath();
                    lastCubicCtrl = null;
                    lastQuadCtrl = null;
                    cmd = null;
                    break;

                default:
                    idx++;
                    break;
            }
        }

        if (currentPath.Count > 1) paths.Add(currentPath);
        return paths;
    }

    private static void AddCubicBezier(VectorPath path, Point2D p0, Point2D p1, Point2D p2, Point2D p3, int segments = 16)
    {
        for (int i = 1; i <= segments; i++)
        {
            double t = (double)i / segments;
            double u = 1.0 - t;
            double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
            double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
            path.Add(new Point2D(x, y));
        }
    }

    private static void AddQuadBezier(VectorPath path, Point2D p0, Point2D p1, Point2D p2, int segments = 12)
    {
        for (int i = 1; i <= segments; i++)
        {
            double t = (double)i / segments;
            double u = 1.0 - t;
            double x = u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X;
            double y = u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y;
            path.Add(new Point2D(x, y));
        }
    }

    private static bool TryParseDouble(string? s, out double val, double fallback = 0)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            return true;
        val = fallback;
        return false;
    }
}
