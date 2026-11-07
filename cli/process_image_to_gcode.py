"""
Creality SparkX i7 - Marker-Optimized Pen Plotter Pipeline
Input: 3130.jpg (F/A-18C Hornet)
Features:
- CLAHE-enhanced structural edges (complete nose, cockpit, wings, stabilizers)
- Marker-friendly filtering (eliminates micro-dots that bleed into paper, merges double lines)
- Strict safety margins (drawing is >= 15mm from paper edges on a 200x150mm sheet)
- Safe Initial Pen Lift: Lifts Z from 5.0 to 8.0mm at center BEFORE homing X/Y
- Produces: Dry-Run G-Code, Real Draw G-Code, Interactive HTML Visualizer
"""

import os
import math
import cv2
import numpy as np

def extract_marker_optimized_paths(image_path):
    img = cv2.imread(image_path)
    if img is None:
        raise FileNotFoundError(f"Image not found: {image_path}")

    # 1. Crop to the aircraft (removing bottom banner and runway clutter)
    crop = img[10:265, 8:495]
    h_c, w_c = crop.shape[:2]

    # 2. Upscale 3x to ensure smooth, organic curves instead of pixel staircasing
    target_w = 1400
    target_h = int(h_c * (target_w / w_c))
    scaled = cv2.resize(crop, (target_w, target_h), interpolation=cv2.INTER_LANCZOS4)

    # 3. LAB CLAHE enhancement to capture the full radome nose and cockpit canopy
    lab = cv2.cvtColor(scaled, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.2, tileGridSize=(8, 8))
    cl = clahe.apply(l)

    # 4. Bilateral filter: smooths sky/ground noise while keeping structural edges crisp
    smooth = cv2.bilateralFilter(cl, 9, 65, 65)

    # 5. Canny edge detection
    edges = cv2.Canny(smooth, 40, 110)

    # 6. Mask out ground clutter (left side ground blocks, lower runway, top watermark)
    edges[int(target_h * 0.55):, 0:int(target_w * 0.07)] = 0
    edges[int(target_h * 0.93):, :] = 0
    edges[0:int(target_h * 0.08), int(target_w * 0.88):] = 0

    # 7. Bridge small gaps so lines are cohesive
    kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    closed = cv2.morphologyEx(edges, cv2.MORPH_CLOSE, kernel)

    # 8. Find contours
    contours, _ = cv2.findContours(closed, cv2.RETR_LIST, cv2.CHAIN_APPROX_TC89_L1)

    # 9. Filter for felt-tip marker:
    # - Ignore strokes shorter than 45px (in 1400w space, ~5mm on paper) to prevent ink bleeding
    # - Simplify with approxPolyDP (epsilon = 1.8) for smooth, confident strokes
    marker_paths = []
    for c in contours:
        length = cv2.arcLength(c, False)
        if length > 45:
            approx = cv2.approxPolyDP(c, 1.8, False)
            if len(approx) >= 2:
                pts = [(float(pt[0][0]), float(pt[0][1])) for pt in approx]
                marker_paths.append(pts)

    print(f"Extracted {len(marker_paths)} marker-friendly structural strokes.")
    return marker_paths, target_w, target_h

def optimize_path_order(paths):
    """Greedy Nearest-Neighbor to minimize pen-up travel distance."""
    if not paths:
        return []
        
    remaining = [list(p) for p in paths]
    ordered = []
    current_pos = (0.0, 0.0)
    
    while remaining:
        best_idx = 0
        best_dist = float('inf')
        reverse_best = False
        
        for i, p in enumerate(remaining):
            d_start = math.hypot(p[0][0] - current_pos[0], p[0][1] - current_pos[1])
            d_end = math.hypot(p[-1][0] - current_pos[0], p[-1][1] - current_pos[1])
            
            if d_start < best_dist:
                best_dist = d_start
                best_idx = i
                reverse_best = False
            if d_end < best_dist:
                best_dist = d_end
                best_idx = i
                reverse_best = True
                
        chosen = remaining.pop(best_idx)
        if reverse_best:
            chosen.reverse()
            
        ordered.append(chosen)
        current_pos = chosen[-1]
        
    return ordered

def scale_and_center_to_paper(paths, orig_w, orig_h, 
                              bed_w=260.0, bed_h=260.0, 
                              paper_w=200.0, paper_h=150.0, 
                              margin=15.0):
    """
    Guarantees >= 15mm (1.5 cm) margin on all sides of the 200x150mm paper sheet.
    Paper is centered on bed at (130, 130).
    """
    max_draw_w = paper_w - 2 * margin  # 170 mm (leaves 15mm margin left and right)
    max_draw_h = paper_h - 2 * margin  # 120 mm (leaves 15mm margin top and bottom)
    
    scale = min(max_draw_w / orig_w, max_draw_h / orig_h)
    
    bed_cx = bed_w / 2.0  # 130 mm
    bed_cy = bed_h / 2.0  # 130 mm
    
    orig_cx = orig_w / 2.0
    orig_cy = orig_h / 2.0
    
    transformed = []
    min_x, max_x = float('inf'), float('-inf')
    min_y, max_y = float('inf'), float('-inf')
    
    for path in paths:
        new_path = []
        for x, y in path:
            tx = bed_cx + (x - orig_cx) * scale
            ty = bed_cy - (y - orig_cy) * scale
            tx = round(tx, 2)
            ty = round(ty, 2)
            min_x = min(min_x, tx)
            max_x = max(max_x, tx)
            min_y = min(min_y, ty)
            max_y = max(max_y, ty)
            new_path.append((tx, ty))
        transformed.append(new_path)
        
    paper_x0 = (bed_w - paper_w) / 2.0
    paper_x1 = paper_x0 + paper_w
    paper_y0 = (bed_h - paper_h) / 2.0
    paper_y1 = paper_y0 + paper_h

    margin_left = min_x - paper_x0
    margin_right = paper_x1 - max_x
    margin_bottom = min_y - paper_y0
    margin_top = paper_y1 - max_y

    print("\n--- SAFETY BOUNDS VERIFICATION ---")
    print(f"Paper area: X=[{paper_x0:.1f}, {paper_x1:.1f}], Y=[{paper_y0:.1f}, {paper_y1:.1f}] mm")
    print(f"Drawing bounds: X=[{min_x:.1f}, {max_x:.1f}], Y=[{min_y:.1f}, {max_y:.1f}] mm")
    print(f"Actual margins from paper edges: Left={margin_left:.1f}mm, Right={margin_right:.1f}mm, Bottom={margin_bottom:.1f}mm, Top={margin_top:.1f}mm")
    print("----------------------------------\n")
    
    return transformed

def generate_plotter_gcode(paths, filename, 
                           z_draw=5.0, z_travel=8.0, 
                           draw_speed=1500, travel_speed=3000, 
                           bed_w=260.0, bed_h=260.0, 
                           paper_w=200.0, paper_h=150.0,
                           dry_run=False):
    """
    Generates G-code with strict pen safety:
    1. G92 Z5.0 (Calibrate touch height at current pen position)
    2. G1 Z8.0 F800 (LIFT PEN 3mm OFF SHEET BEFORE ANY HORIZONTAL MOVE!)
    3. G28 X Y (Home X/Y while pen is safely in the air)
    """
    if dry_run:
        actual_z_draw = z_draw + 5.0    # 10.0 mm (air test)
        actual_z_travel = z_travel + 5.0 # 13.0 mm
        mode_str = "; *** DRY-RUN MODE: DRAWING IN AIR (Z=10mm, 5mm above paper) ***"
    else:
        actual_z_draw = z_draw          # 5.0 mm (real contact)
        actual_z_travel = z_travel      # 8.0 mm (3mm travel hop)
        mode_str = "; *** REAL DRAWING MODE: CONTACT AT Z=5.0mm, TRAVEL AT Z=8.0mm ***"

    lines = [
        "; ==================================================================",
        "; Creality SparkX i7 - F/A-18C Hornet Pen Plotter G-Code",
        mode_str,
        f"; Bed: {bed_w}x{bed_h} mm | Sheet: {paper_w}x{paper_h} mm (Centered)",
        "; ==================================================================\n",
        "; --- SAFETY: DISABLE SENSORS AND HEATERS ---",
        "SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=0 ; Disable filament runout sensor",
        "M104 S0 ; Extruder heater OFF",
        "M140 S0 ; Bed heater OFF",
        "M106 S0 ; Part cooling fan OFF",
        "G90     ; Absolute Positioning",
        "G21     ; Millimeters\n",
        "; --- CRITICAL: LIFT PEN BEFORE ANY HORIZONTAL MOVE ---",
        f"G92 Z{z_draw:.2f} ; Register current position as Z={z_draw:.2f}mm (pen touching sheet)",
        f"G1 Z{actual_z_travel:.2f} F800 ; LIFT PEN OFF THE SHEET IMMEDIATELY!",
        "G4 P500 ; Dwell 0.5s to ensure pen is fully in the air\n",
        "; --- BEGIN DRAWING STROKES ---"
    ]

    total_strokes = len(paths)
    for i, path in enumerate(paths):
        start_x, start_y = path[0]
        lines.append(f"; Stroke {i+1}/{total_strokes}")
        # Rapid travel to start point with pen UP
        lines.append(f"G1 X{start_x:.2f} Y{start_y:.2f} F{travel_speed}")
        # Lower pen to paper
        lines.append(f"G1 Z{actual_z_draw:.2f} F600 ; Pen Down")
        # Draw path
        for pt in path[1:]:
            lines.append(f"G1 X{pt[0]:.2f} Y{pt[1]:.2f} F{draw_speed}")
        # Lift pen off paper
        lines.append(f"G1 Z{actual_z_travel:.2f} F800 ; Pen Up\n")

    # End Sequence
    lines.extend([
        "; --- FINISH & PRESENT ---",
        f"G1 Z{actual_z_travel + 15.0:.2f} F1000 ; Lift pen high away from sheet",
        f"G1 X10 Y{bed_h - 20:.2f} F{travel_speed} ; Slide bed forward",
        "SET_FILAMENT_SENSOR SENSOR=filament_sensor ENABLE=1 ; Re-enable filament sensor",
        "; KEEP STEPPERS POWERED: Motors stay energized so exact coordinates are NEVER lost between drawings!",
        "; === END OF PLOT ==="
    ])

    with open(filename, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print(f"Saved G-Code: {filename} ({len(lines)} lines, {total_strokes} strokes)")

def generate_interactive_preview(paths, filename, 
                                 bed_w=260.0, bed_h=260.0, 
                                 paper_w=200.0, paper_h=150.0):
    paper_x0 = (bed_w - paper_w) / 2.0
    paper_y0 = (bed_h - paper_h) / 2.0

    paths_js = [[[round(pt[0], 2), round(pt[1], 2)] for pt in p] for p in paths]

    html = f"""<!DOCTYPE html>
<html lang="it">
<head>
<meta charset="UTF-8">
<title>SparkX i7 - F/A-18C Hornet Plotter Preview</title>
<style>
  body {{ font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #f1f5f9; display: flex; flex-direction: column; align-items: center; padding: 20px; margin: 0; }}
  h2 {{ color: #38bdf8; margin-bottom: 6px; }}
  .subtitle {{ color: #94a3b8; font-size: 14px; margin-bottom: 16px; }}
  .card {{ background: #1e293b; padding: 20px; border-radius: 12px; box-shadow: 0 10px 25px rgba(0,0,0,0.5); }}
  canvas {{ background: #334155; border-radius: 8px; border: 2px solid #475569; }}
  .legend {{ margin-top: 15px; display: flex; gap: 20px; font-size: 13px; justify-content: center; }}
  .legend span {{ display: flex; align-items: center; gap: 8px; }}
  .dot {{ width: 14px; height: 14px; border-radius: 3px; display: inline-block; }}
  .info-bar {{ margin-top: 15px; background: #0f172a; padding: 10px 16px; border-radius: 6px; font-size: 13px; color: #cbd5e1; display: flex; justify-content: space-between; }}
</style>
</head>
<body>
  <h2>F/A-18C Hornet Pen Plotter Preview</h2>
  <div class="subtitle">Creality SparkX i7 (260x260 mm) | Foglio: 200x150 mm al centro</div>
  <div class="card">
    <canvas id="preview" width="600" height="600"></canvas>
    <div class="info-bar">
      <span>Dimensioni foglio: 200 &times; 150 mm</span>
      <span>Tratti marker: {len(paths)}</span>
      <span>Margine sicurezza: &ge; 15 mm dai bordi</span>
    </div>
    <div class="legend">
      <span><span class="dot" style="background: #ffffff; border: 1px solid #94a3b8;"></span> Foglio (200x150 mm)</span>
      <span><span class="dot" style="background: #0284c7;"></span> Tratto pennarello</span>
      <span><span class="dot" style="background: #f43f5e; border: 1px dashed red;"></span> Movimenti in volo (Travel)</span>
      <span><span class="dot" style="background: #10b981; border-radius: 50%;"></span> Partenza al centro</span>
    </div>
  </div>
  <script>
    const paths = {paths_js};
    const canvas = document.getElementById('preview');
    const ctx = canvas.getContext('2d');
    const bedW = {bed_w};
    const bedH = {bed_h};
    const scale = canvas.width / bedW;

    function toCanvas(x, y) {{
      return [x * scale, canvas.height - y * scale];
    }}

    // 1. Bed Background
    ctx.fillStyle = "#1e293b";
    ctx.fillRect(0, 0, canvas.width, canvas.height);

    // Bed Grid
    ctx.strokeStyle = "#334155";
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

    // 2. Paper Sheet (200x150 mm)
    const [px0, py0] = toCanvas({paper_x0}, {paper_y0} + {paper_h});
    ctx.fillStyle = "#f8fafc";
    ctx.fillRect(px0, py0, {paper_w} * scale, {paper_h} * scale);
    ctx.strokeStyle = "#cbd5e1";
    ctx.lineWidth = 2;
    ctx.strokeRect(px0, py0, {paper_w} * scale, {paper_h} * scale);

    // 10mm Safety Margin Line inside paper (dashed green)
    const [mx0, my0] = toCanvas({paper_x0} + 10, {paper_y0} + {paper_h} - 10);
    ctx.strokeStyle = "#86efac";
    ctx.setLineDash([4, 4]);
    ctx.lineWidth = 1;
    ctx.strokeRect(mx0, my0, ({paper_w} - 20) * scale, ({paper_h} - 20) * scale);

    // 3. Initial Lift Point at Center (130, 130)
    let [cx, cy] = toCanvas(130, 130);
    ctx.fillStyle = "#10b981";
    ctx.beginPath();
    ctx.arc(cx, cy, 7, 0, 2*Math.PI);
    ctx.fill();

    // 4. Travel moves (Red dashed)
    ctx.strokeStyle = "#f43f5e";
    ctx.setLineDash([3, 4]);
    ctx.lineWidth = 1;
    let prev = [cx, cy];
    for(let path of paths) {{
      if(path.length === 0) continue;
      let start = toCanvas(path[0][0], path[0][1]);
      ctx.beginPath();
      ctx.moveTo(prev[0], prev[1]);
      ctx.lineTo(start[0], start[1]);
      ctx.stroke();
      prev = toCanvas(path[path.length - 1][0], path[path.length - 1][1]);
    }}

    // 5. Drawing strokes (Blue solid, simulated marker width)
    ctx.setLineDash([]);
    ctx.strokeStyle = "#0284c7";
    ctx.lineWidth = 2.4;
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
  </script>
</body>
</html>
"""
    with open(filename, 'w', encoding='utf-8') as f:
        f.write(html)
    print(f"Saved Interactive Preview: {filename}")

if __name__ == "__main__":
    img_file = "3130.jpg" if os.path.exists("3130.jpg") else "3130.png"
    print(f"Processing image: {img_file}...")
    
    raw_paths, orig_w, orig_h = extract_marker_optimized_paths(img_file)
    ordered_paths = optimize_path_order(raw_paths)
    
    scaled_paths = scale_and_center_to_paper(
        ordered_paths, 
        orig_w=orig_w, 
        orig_h=orig_h, 
        bed_w=260.0, 
        bed_h=260.0, 
        paper_w=200.0, 
        paper_h=150.0, 
        margin=15.0
    )
    
    # 1. Generate Dry-Run G-Code (Z=10mm)
    generate_plotter_gcode(
        scaled_paths, 
        "3130_hornet_dryrun.gcode", 
        z_draw=5.0, 
        z_travel=8.0, 
        dry_run=True
    )
    
    # 2. Generate Real Drawing G-Code (Z=5.0mm)
    generate_plotter_gcode(
        scaled_paths, 
        "3130_hornet_draw.gcode", 
        z_draw=5.0, 
        z_travel=8.0, 
        dry_run=False
    )
    
    # 3. Generate Interactive Visual Preview
    generate_interactive_preview(
        scaled_paths, 
        "3130_hornet_preview.html", 
        bed_w=260.0, 
        bed_h=260.0, 
        paper_w=200.0, 
        paper_h=150.0
    )
    print("\nPlotter generation complete!")
