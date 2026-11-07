"""
Pen Plotter G-Code Generator for 3D Printers (SparkX i7 / Marlin / Klipper)
Converts SVG vector files to pure, safe Plotter G-Code.
Zero external dependencies (pure Python standard library).
"""

import sys
import os
import re
import math
import argparse
import xml.etree.ElementTree as ET

def tokenize_path(d):
    """Tokenize an SVG path data string into commands and numeric arguments."""
    token_pattern = re.compile(r'([MmLlHhVvCcSsQqTtAaZz])|([-+]?(?:\d*\.\d+|\d+)(?:[eE][-+]?\d+)?)')
    tokens = []
    for match in token_pattern.finditer(d):
        cmd, num = match.groups()
        if cmd:
            tokens.append(cmd)
        elif num:
            tokens.append(float(num))
    return tokens

def cubic_bezier_points(p0, p1, p2, p3, num_segments=16):
    """Generate linear segments approximating a cubic Bezier curve."""
    pts = []
    for i in range(1, num_segments + 1):
        t = i / num_segments
        u = 1.0 - t
        x = (u**3 * p0[0] + 
             3 * u**2 * t * p1[0] + 
             3 * u * t**2 * p2[0] + 
             t**3 * p3[0])
        y = (u**3 * p0[1] + 
             3 * u**2 * t * p1[1] + 
             3 * u * t**2 * p2[1] + 
             t**3 * p3[1])
        pts.append((x, y))
    return pts

def quadratic_bezier_points(p0, p1, p2, num_segments=12):
    """Generate linear segments approximating a quadratic Bezier curve."""
    pts = []
    for i in range(1, num_segments + 1):
        t = i / num_segments
        u = 1.0 - t
        x = u**2 * p0[0] + 2 * u * t * p1[0] + t**2 * p2[0]
        y = u**2 * p0[1] + 2 * u * t * p1[1] + t**2 * p2[1]
        pts.append((x, y))
    return pts

def arc_to_points(p0, rx, ry, phi_deg, large_arc, sweep, p1, num_segments=16):
    """Approximate an SVG elliptical arc as line segments."""
    x0, y0 = p0
    x1, y1 = p1
    if x0 == x1 and y0 == y1:
        return []
    rx = abs(rx)
    ry = abs(ry)
    if rx == 0 or ry == 0:
        return [p1]

    phi = math.radians(phi_deg % 360.0)
    cos_phi = math.cos(phi)
    sin_phi = math.sin(phi)

    # Step 1: Compute (x1', y1')
    dx = (x0 - x1) / 2.0
    dy = (y0 - y1) / 2.0
    x1_prime = cos_phi * dx + sin_phi * dy
    y1_prime = -sin_phi * dx + cos_phi * dy

    # Correct radii
    lambda_val = (x1_prime**2) / (rx**2) + (y1_prime**2) / (ry**2)
    if lambda_val > 1:
        rx *= math.sqrt(lambda_val)
        ry *= math.sqrt(lambda_val)

    # Step 2: Center (cx', cy')
    num = (rx**2 * ry**2) - (rx**2 * y1_prime**2) - (ry**2 * x1_prime**2)
    denom = (rx**2 * y1_prime**2) + (ry**2 * x1_prime**2)
    factor = 0 if denom == 0 else math.sqrt(max(0, num / denom))
    if large_arc == sweep:
        factor = -factor
    cx_prime = factor * (rx * y1_prime / ry)
    cy_prime = factor * (-ry * x1_prime / rx)

    # Step 3: Center (cx, cy)
    cx = cos_phi * cx_prime - sin_phi * cy_prime + (x0 + x1) / 2.0
    cy = sin_phi * cx_prime + cos_phi * cy_prime + (y0 + y1) / 2.0

    # Step 4: Angles theta1 and delta_theta
    def vector_angle(ux, uy, vx, vy):
        n_u = math.hypot(ux, uy)
        n_v = math.hypot(vx, vy)
        if n_u == 0 or n_v == 0:
            return 0.0
        val = max(-1.0, min(1.0, (ux * vx + uy * vy) / (n_u * n_v)))
        ang = math.acos(val)
        if (ux * vy - uy * vx) < 0:
            ang = -ang
        return ang

    u = ((x1_prime - cx_prime) / rx, (y1_prime - cy_prime) / ry)
    theta1 = vector_angle(1, 0, u[0], u[1])
    v = ((-x1_prime - cx_prime) / rx, (-y1_prime - cy_prime) / ry)
    d_theta = vector_angle(u[0], u[1], v[0], v[1])
    if not sweep and d_theta > 0:
        d_theta -= 2 * math.pi
    elif sweep and d_theta < 0:
        d_theta += 2 * math.pi

    pts = []
    for i in range(1, num_segments + 1):
        th = theta1 + d_theta * (i / num_segments)
        px_prime = rx * math.cos(th)
        py_prime = ry * math.sin(th)
        px = cos_phi * px_prime - sin_phi * py_prime + cx
        py = sin_phi * px_prime + cos_phi * py_prime + cy
        pts.append((px, py))
    return pts

def parse_svg_path(d):
    """
    Parses an SVG path 'd' string into a list of continuous polylines.
    Returns: list of paths, where each path is [(x, y), (x, y), ...]
    """
    tokens = tokenize_path(d)
    paths = []
    current_path = []
    
    current_pt = (0.0, 0.0)
    start_subpath_pt = (0.0, 0.0)
    last_cubic_ctrl = None
    last_quad_ctrl = None
    
    idx = 0
    cmd = None
    
    while idx < len(tokens):
        token = tokens[idx]
        if isinstance(token, str):
            cmd = token
            idx += 1
        
        if cmd is None:
            break
            
        if cmd in 'Mm':
            x = tokens[idx]
            y = tokens[idx+1]
            idx += 2
            if cmd == 'm':
                x += current_pt[0]
                y += current_pt[1]
            
            if current_path:
                paths.append(current_path)
            current_path = [(x, y)]
            current_pt = (x, y)
            start_subpath_pt = current_pt
            last_cubic_ctrl = None
            last_quad_ctrl = None
            # Successive numbers after M are treated as L/l
            cmd = 'l' if cmd == 'm' else 'L'
            
        elif cmd in 'Ll':
            x = tokens[idx]
            y = tokens[idx+1]
            idx += 2
            if cmd == 'l':
                x += current_pt[0]
                y += current_pt[1]
            current_pt = (x, y)
            current_path.append(current_pt)
            last_cubic_ctrl = None
            last_quad_ctrl = None
            
        elif cmd in 'Hh':
            x = tokens[idx]
            idx += 1
            if cmd == 'h':
                x += current_pt[0]
            current_pt = (x, current_pt[1])
            current_path.append(current_pt)
            last_cubic_ctrl = None
            last_quad_ctrl = None
            
        elif cmd in 'Vv':
            y = tokens[idx]
            idx += 1
            if cmd == 'v':
                y += current_pt[1]
            current_pt = (current_pt[0], y)
            current_path.append(current_pt)
            last_cubic_ctrl = None
            last_quad_ctrl = None
            
        elif cmd in 'Cc':
            x1, y1, x2, y2, x, y = tokens[idx:idx+6]
            idx += 6
            if cmd == 'c':
                x1 += current_pt[0]; y1 += current_pt[1]
                x2 += current_pt[0]; y2 += current_pt[1]
                x += current_pt[0]; y += current_pt[1]
            pts = cubic_bezier_points(current_pt, (x1, y1), (x2, y2), (x, y))
            current_path.extend(pts)
            current_pt = (x, y)
            last_cubic_ctrl = (x2, y2)
            last_quad_ctrl = None
            
        elif cmd in 'Ss':
            x2, y2, x, y = tokens[idx:idx+4]
            idx += 4
            if cmd == 's':
                x2 += current_pt[0]; y2 += current_pt[1]
                x += current_pt[0]; y += current_pt[1]
            if last_cubic_ctrl:
                x1 = 2 * current_pt[0] - last_cubic_ctrl[0]
                y1 = 2 * current_pt[1] - last_cubic_ctrl[1]
            else:
                x1, y1 = current_pt
            pts = cubic_bezier_points(current_pt, (x1, y1), (x2, y2), (x, y))
            current_path.extend(pts)
            current_pt = (x, y)
            last_cubic_ctrl = (x2, y2)
            last_quad_ctrl = None
            
        elif cmd in 'Qq':
            x1, y1, x, y = tokens[idx:idx+4]
            idx += 4
            if cmd == 'q':
                x1 += current_pt[0]; y1 += current_pt[1]
                x += current_pt[0]; y += current_pt[1]
            pts = quadratic_bezier_points(current_pt, (x1, y1), (x, y))
            current_path.extend(pts)
            current_pt = (x, y)
            last_quad_ctrl = (x1, y1)
            last_cubic_ctrl = None
            
        elif cmd in 'Tt':
            x, y = tokens[idx:idx+2]
            idx += 2
            if cmd == 't':
                x += current_pt[0]; y += current_pt[1]
            if last_quad_ctrl:
                x1 = 2 * current_pt[0] - last_quad_ctrl[0]
                y1 = 2 * current_pt[1] - last_quad_ctrl[1]
            else:
                x1, y1 = current_pt
            pts = quadratic_bezier_points(current_pt, (x1, y1), (x, y))
            current_path.extend(pts)
            current_pt = (x, y)
            last_quad_ctrl = (x1, y1)
            last_cubic_ctrl = None
            
        elif cmd in 'Aa':
            rx, ry, phi, large_arc, sweep, x, y = tokens[idx:idx+7]
            idx += 7
            if cmd == 'a':
                x += current_pt[0]; y += current_pt[1]
            pts = arc_to_points(current_pt, rx, ry, phi, bool(large_arc), bool(sweep), (x, y))
            current_path.extend(pts)
            current_pt = (x, y)
            last_cubic_ctrl = None
            last_quad_ctrl = None
            
        elif cmd in 'Zz':
            if start_subpath_pt != current_pt:
                current_path.append(start_subpath_pt)
                current_pt = start_subpath_pt
            paths.append(current_path)
            current_path = []
            last_cubic_ctrl = None
            last_quad_ctrl = None
            cmd = None
        else:
            idx += 1
            
    if current_path:
        paths.append(current_path)
    return [p for p in paths if len(p) > 1]

def extract_paths_from_svg(svg_file):
    """Extract all drawable paths from an SVG file."""
    tree = ET.parse(svg_file)
    root = tree.getroot()
    
    # Strip namespaces
    for el in root.iter():
        if '}' in el.tag:
            el.tag = el.tag.split('}', 1)[1]
            
    all_paths = []
    
    # 1. <path d="..." />
    for el in root.iter('path'):
        d = el.attrib.get('d', '')
        if d:
            all_paths.extend(parse_svg_path(d))
            
    # 2. <line x1="" y1="" x2="" y2="" />
    for el in root.iter('line'):
        try:
            x1 = float(el.attrib.get('x1', 0))
            y1 = float(el.attrib.get('y1', 0))
            x2 = float(el.attrib.get('x2', 0))
            y2 = float(el.attrib.get('y2', 0))
            all_paths.append([(x1, y1), (x2, y2)])
        except ValueError:
            pass
            
    # 3. <rect x="" y="" width="" height="" />
    for el in root.iter('rect'):
        try:
            x = float(el.attrib.get('x', 0))
            y = float(el.attrib.get('y', 0))
            w = float(el.attrib.get('width', 0))
            h = float(el.attrib.get('height', 0))
            if w > 0 and h > 0:
                all_paths.append([(x, y), (x+w, y), (x+w, y+h), (x, y+h), (x, y)])
        except ValueError:
            pass
            
    # 4. <circle cx="" cy="" r="" />
    for el in root.iter('circle'):
        try:
            cx = float(el.attrib.get('cx', 0))
            cy = float(el.attrib.get('cy', 0))
            r = float(el.attrib.get('r', 0))
            if r > 0:
                pts = [(cx + r * math.cos(2*math.pi*i/32), cy + r * math.sin(2*math.pi*i/32)) for i in range(33)]
                all_paths.append(pts)
        except ValueError:
            pass

    # 5. <polyline points="..." /> and <polygon points="..." />
    for tag in ['polyline', 'polygon']:
        for el in root.iter(tag):
            pts_str = el.attrib.get('points', '')
            nums = re.findall(r'[-+]?(?:\d*\.\d+|\d+)', pts_str)
            if len(nums) >= 4:
                coords = [(float(nums[i]), float(nums[i+1])) for i in range(0, len(nums)-1, 2)]
                if tag == 'polygon' and coords and coords[0] != coords[-1]:
                    coords.append(coords[0])
                all_paths.append(coords)
                
    return all_paths

def transform_and_center_paths(paths, bed_w=260, bed_h=260, margin=30, max_size=180):
    """
    Scales and centers SVG paths onto the printer bed.
    Flips Y-axis (SVG top-left origin -> 3D printer bottom-left origin).
    """
    if not paths:
        return []
        
    min_x = min(min(p[0] for p in path) for path in paths)
    max_x = max(max(p[0] for p in path) for path in paths)
    min_y = min(min(p[1] for p in path) for path in paths)
    max_y = max(max(p[1] for p in path) for path in paths)
    
    orig_w = max_x - min_x
    orig_h = max_y - min_y
    
    if orig_w == 0 or orig_h == 0:
        return []
        
    avail_w = bed_w - 2 * margin
    avail_h = bed_h - 2 * margin
    
    if max_size:
        avail_w = min(avail_w, max_size)
        avail_h = min(avail_h, max_size)
        
    scale = min(avail_w / orig_w, avail_h / orig_h)
    
    # SVG y is 0 at top; 3D printer y is 0 at bottom front.
    # We flip Y so the drawing is right side up.
    transformed_paths = []
    target_cx = bed_w / 2.0
    target_cy = bed_h / 2.0
    
    orig_cx = (min_x + max_x) / 2.0
    orig_cy = (min_y + max_y) / 2.0
    
    for path in paths:
        new_path = []
        for (x, y) in path:
            tx = target_cx + (x - orig_cx) * scale
            # Invert Y relative to center
            ty = target_cy - (y - orig_cy) * scale
            new_path.append((round(tx, 2), round(ty, 2)))
        transformed_paths.append(new_path)
        
    return transformed_paths

def generate_gcode(paths, filename, z_draw=5.0, z_travel=8.0, 
                   draw_speed=1500, travel_speed=3000, 
                   bed_w=220, bed_h=220, dry_run=False):
    """Generates clean, safe G-Code for the pen plotter."""
    
    if dry_run:
        # In dry run, we raise both drawing and travel heights by 5mm (air drawing)
        actual_z_draw = z_draw + 5.0
        actual_z_travel = z_travel + 5.0
        mode_comment = f"; *** DRY RUN MODE ACTIVATED (AIR DRAWING) ***\n; Drawing at Z={actual_z_draw}mm (safe 5mm above sheet)"
    else:
        actual_z_draw = z_draw
        actual_z_travel = z_travel
        mode_comment = f"; *** REAL PLOTTING MODE ***\n; Drawing at Z={actual_z_draw}mm, Travel at Z={actual_z_travel}mm"
        
    lines = []
    lines.append("; ======================================================")
    lines.append("; SparkX i7 Pen Plotter G-Code")
    lines.append(f"; Generated by PenPlotter Safe Generator")
    lines.append(mode_comment)
    lines.append(f"; Bed Size: {bed_w}x{bed_h} mm")
    lines.append("; ======================================================\n")
    
    # Safety Header
    lines.append("; --- SAFETY INITIALIZATION ---")
    lines.append("SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=0 ; Disable filament sensor dynamically")
    lines.append("M104 S0 ; Ensure Hotend heater is OFF")
    lines.append("M140 S0 ; Ensure Bed heater is OFF")
    lines.append("M106 S0 ; Ensure Fan is OFF")
    lines.append("G90     ; Absolute Positioning")
    lines.append("G21     ; Millimeter units\n")
    
    # Establish coordinate system:
    # If the user already placed the pen at Z=5.0 touching the bed,
    # we initialize the machine Z coordinate to 5.0 without physical Z homing!
    lines.append("; --- COORDINATE LOCK & HOMING ---")
    lines.append(f"G92 Z{z_draw:.2f} ; Lock current Z to {z_draw:.2f}mm (Pen touch-off)")
    lines.append(f"G1 Z{actual_z_travel:.2f} F1000 ; LIFT PEN TO TRAVEL HEIGHT IMMEDIATELY")
    lines.append("G28 X Y ; Home X and Y ONLY (Z remains locked and safe!)\n")
    
    lines.append("; --- BEGIN DRAWING ---")
    total_strokes = len(paths)
    
    for i, path in enumerate(paths):
        if not path:
            continue
        start_x, start_y = path[0]
        
        # Move above start point with pen UP
        lines.append(f"; Stroke {i+1}/{total_strokes}")
        lines.append(f"G1 X{start_x:.2f} Y{start_y:.2f} F{travel_speed}")
        
        # Pen DOWN
        lines.append(f"G1 Z{actual_z_draw:.2f} F600 ; Pen Down")
        
        # Draw path
        for (x, y) in path[1:]:
            lines.append(f"G1 X{x:.2f} Y{y:.2f} F{draw_speed}")
            
        # Pen UP
        lines.append(f"G1 Z{actual_z_travel:.2f} F800 ; Pen Up\n")
        
    # End G-Code
    lines.append("; --- FINISH & PRESENT DRAWING ---")
    lines.append(f"G1 Z{actual_z_travel + 10.0:.2f} F1000 ; Lift pen high away from paper")
    lines.append(f"G1 X10 Y{bed_h - 20:.2f} F{travel_speed} ; Present bed forward")
    lines.append("SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=1 ; Re-enable filament sensor for 3D printing")
    lines.append("; KEEP STEPPERS POWERED: Preserve coordinate references")
    lines.append("; === END OF G-CODE ===")
    
    with open(filename, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
        
    print(f"Generated G-Code: {filename} ({len(lines)} lines, {total_strokes} strokes)")

def generate_html_preview(paths, filename, bed_w=220, bed_h=220):
    """Generates a standalone HTML visualizer to preview toolpaths."""
    # Build JS path data
    paths_js = []
    for p in paths:
        pts = [[round(pt[0], 2), round(pt[1], 2)] for pt in p]
        paths_js.append(pts)
        
    html = f"""<!DOCTYPE html>
<html lang="it">
<head>
<meta charset="UTF-8">
<title>Pen Plotter Toolpath Preview</title>
<style>
  body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: #1e1e2e; color: #cdd6f4; display: flex; flex-direction: column; align-items: center; padding: 20px; }}
  h2 {{ margin-bottom: 10px; color: #89b4fa; }}
  .card {{ background: #181825; padding: 20px; border-radius: 12px; box-shadow: 0 4px 20px rgba(0,0,0,0.5); }}
  canvas {{ background: #ffffff; border-radius: 8px; border: 3px solid #313244; box-shadow: 0 2px 10px rgba(0,0,0,0.3); }}
  .legend {{ margin-top: 15px; display: flex; gap: 20px; font-size: 14px; }}
  .legend span {{ display: flex; align-items: center; gap: 8px; }}
  .dot {{ width: 14px; height: 14px; border-radius: 50%; display: inline-block; }}
</style>
</head>
<body>
  <h2>Pen Plotter Toolpath Visualizer (SparkX i7)</h2>
  <div class="card">
    <canvas id="preview" width="550" height="550"></canvas>
    <div class="legend">
      <span><span class="dot" style="background: #2563eb;"></span> Tratto Penna (Drawing)</span>
      <span><span class="dot" style="background: #ef4444; border: 1px dashed red;"></span> Movimento a vuoto (Travel)</span>
      <span><span class="dot" style="background: #10b981;"></span> Origine (0, 0)</span>
    </div>
  </div>
  <script>
    const paths = {paths_js};
    const canvas = document.getElementById('preview');
    const ctx = canvas.getContext('2d');
    const bedW = {bed_w};
    const bedH = {bed_h};
    const scale = canvas.width / bedW;

    // Background paper
    ctx.fillStyle = "#fafafa";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    // Bed Grid
    ctx.strokeStyle = "#e5e7eb";
    ctx.lineWidth = 1;
    for(let i = 0; i <= bedW; i += 20) {{
      ctx.beginPath();
      ctx.moveTo(i * scale, 0);
      ctx.lineTo(i * scale, canvas.height);
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(0, i * scale);
      ctx.lineTo(canvas.width, i * scale);
      ctx.stroke();
    }}

    function toCanvas(x, y) {{
      return [x * scale, canvas.height - y * scale];
    }}

    // Draw Travel moves (Red dashed)
    ctx.strokeStyle = "#ef4444";
    ctx.setLineDash([4, 4]);
    ctx.lineWidth = 1.2;
    let lastPt = [0, 0];
    for(let path of paths) {{
      if(path.length === 0) continue;
      let start = toCanvas(path[0][0], path[0][1]);
      let prev = toCanvas(lastPt[0], lastPt[1]);
      ctx.beginPath();
      ctx.moveTo(prev[0], prev[1]);
      ctx.lineTo(start[0], start[1]);
      ctx.stroke();
      lastPt = path[path.length - 1];
    }}

    // Draw Drawing moves (Blue solid)
    ctx.setLineDash([]);
    ctx.strokeStyle = "#2563eb";
    ctx.lineWidth = 2.5;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    for(let path of paths) {{
      if(path.length === 0) continue;
      ctx.beginPath();
      let [x0, y0] = toCanvas(path[0][0], path[0][1]);
      ctx.moveTo(x0, y0);
      for(let i = 1; i < path.length; i++) {{
        let [xi, yi] = toCanvas(path[i][0], path[i][1]);
        ctx.lineTo(xi, yi);
      }}
      ctx.stroke();
    }}

    // Mark Origin
    let [ox, oy] = toCanvas(0, 0);
    ctx.fillStyle = "#10b981";
    ctx.beginPath();
    ctx.arc(ox + 10, oy - 10, 6, 0, 2*Math.PI);
    ctx.fill();
  </script>
</body>
</html>
"""
    with open(filename, 'w', encoding='utf-8') as f:
        f.write(html)
    print(f"Generated Interactive Preview: {filename}")

def create_sample_svg(filename="sample_calibration.svg"):
    """Creates a sample SVG test pattern for the first test run."""
    svg_content = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="100mm" height="100mm">
  <!-- Outer calibration square -->
  <rect x="5" y="5" width="90" height="90" fill="none" stroke="black" stroke-width="2" />
  <!-- Inner precision circle -->
  <circle cx="50" cy="50" r="35" fill="none" stroke="black" stroke-width="2" />
  <!-- Center Star -->
  <polygon points="50,22 58,38 76,38 62,50 67,68 50,56 33,68 38,50 24,38 42,38" fill="none" stroke="black" stroke-width="2" />
</svg>"""
    with open(filename, 'w', encoding='utf-8') as f:
        f.write(svg_content)
    print(f"Created sample SVG test file: {filename}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Pen Plotter G-Code Generator for 3D Printers")
    parser.add_argument("svg_file", nargs="?", help="Path to input SVG file")
    parser.add_argument("--dry-run", action="store_true", help="Air-drawing mode (5mm above paper) for safety test")
    parser.add_argument("--z-draw", type=float, default=5.0, help="Z height when pen touches paper (default: 5.0 mm)")
    parser.add_argument("--z-travel", type=float, default=8.0, help="Z height for travel pen-up (default: 8.0 mm)")
    parser.add_argument("--bed-w", type=float, default=260.0, help="Bed Width in mm (default: 260 mm for SparkX i7)")
    parser.add_argument("--bed-h", type=float, default=260.0, help="Bed Height in mm (default: 260 mm for SparkX i7)")
    parser.add_argument("--margin", type=float, default=30.0, help="Margin from bed edges in mm (default: 30 mm)")
    parser.add_argument("--max-size", type=float, default=180.0, help="Max width/height of drawing in mm (default: 180 mm)")
    parser.add_argument("--draw-speed", type=int, default=1500, help="Drawing feedrate in mm/min (default: 1500)")
    parser.add_argument("--travel-speed", type=int, default=3000, help="Travel feedrate in mm/min (default: 3000)")
    parser.add_argument("-o", "--output", help="Output .gcode file name")
    
    args = parser.parse_args()
    
    if not args.svg_file:
        print("No SVG file provided. Creating 'sample_calibration.svg' as a test pattern...")
        create_sample_svg("sample_calibration.svg")
        args.svg_file = "sample_calibration.svg"
        
    if not os.path.exists(args.svg_file):
        print(f"Error: File '{args.svg_file}' not found.")
        sys.exit(1)
        
    print(f"Reading SVG: {args.svg_file}...")
    raw_paths = extract_paths_from_svg(args.svg_file)
    print(f"Extracted {len(raw_paths)} vector paths.")
    
    if not raw_paths:
        print("Error: No drawable paths found in SVG file.")
        sys.exit(1)
        
    fitted_paths = transform_and_center_paths(
        raw_paths, 
        bed_w=args.bed_w, 
        bed_h=args.bed_h, 
        margin=args.margin, 
        max_size=args.max_size
    )
    
    base_name = os.path.splitext(os.path.basename(args.svg_file))[0]
    out_gcode = args.output or f"{base_name}_{'dryrun' if args.dry_run else 'draw'}.gcode"
    out_html = f"{base_name}_preview.html"
    
    generate_gcode(
        fitted_paths, 
        out_gcode, 
        z_draw=args.z_draw, 
        z_travel=args.z_travel,
        draw_speed=args.draw_speed,
        travel_speed=args.travel_speed,
        bed_w=args.bed_w,
        bed_h=args.bed_h,
        dry_run=args.dry_run
    )
    
    generate_html_preview(fitted_paths, out_html, bed_w=args.bed_w, bed_h=args.bed_h)
    print("\nDone! Safe G-Code and Preview ready.")
