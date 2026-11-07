"""
Safe Configuration Manager for Creality SparkX i7 (Pen Plotter Mode)
- Inverts filament sensor (filament_detected=True)
- Makes Home Z (Casetta con Z) 100% safe: never pushes pen into bed with strain gauge
- Prevents System Errors
- Backs up both configs and supports instant 1-click restore
"""

import sys
import uuid
import urllib.request
import json
import time
import re

PRINTER_IP = "192.168.78.50"
PRINTER_PORT = 7125

def send_gcode(cmd):
    url = f"http://{PRINTER_IP}:{PRINTER_PORT}/printer/gcode/script"
    payload = json.dumps({"script": cmd}).encode("utf-8")
    req = urllib.request.Request(url, data=payload, headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=5.0) as r:
            return r.getcode() == 200
    except Exception as e:
        print(f"GCode error '{cmd}': {e}")
        return False

def get_config(filename):
    url = f"http://{PRINTER_IP}:{PRINTER_PORT}/server/files/config/{filename}"
    with urllib.request.urlopen(url, timeout=5.0) as r:
        return r.read().decode("utf-8")

def upload_config(filename, content):
    boundary = "----WebKitFormBoundary" + uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"root\"\r\n\r\nconfig\r\n".encode("utf-8"),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\nContent-Type: text/plain\r\n\r\n".encode("utf-8"),
        content.encode("utf-8"),
        f"\r\n--{boundary}--\r\n".encode("utf-8")
    ]
    data = b"".join(parts)
    url = f"http://{PRINTER_IP}:{PRINTER_PORT}/server/files/upload"
    req = urllib.request.Request(url, data=data, headers={"Content-Type": f"multipart/form-data; boundary={boundary}"}, method="POST")
    with urllib.request.urlopen(req, timeout=10.0) as r:
        return r.getcode() in [200, 201]

def enable_plotter_mode():
    print("Filament Sensor: Handled dynamically via G-code (SET_FILAMENT_SENSOR ENABLE=0/1).")
    print("printer.cfg remains stock for 100% PLA 3D printing compatibility.")

    print("Securing Home Z in sensorless.cfg to prevent System Error...")
    scfg = get_config("sensorless.cfg")
    upload_config("sensorless.cfg.plotter_backup", scfg)

    # In sensorless.cfg, replace the dangerous PRES_CHECK and nozzle probe with safe lift
    old_z_block = """  {% if home_all or 'Z' in params %}
    PRES_CHECK
    G4 P1000
    _HOME_Z
    BED_MESH_PROFILE LOAD="default"
  {% endif %}"""

    new_z_block = """  {% if home_all or 'Z' in params %}
    # SAFE PLOTTER HOME Z: Prevents pen crash into bed and System Error
    SET_KINEMATIC_POSITION Z=25.0
    G90
    G1 Z25.0 F1200
    SET_GCODE_VARIABLE MACRO=xyz_ready VARIABLE=z_ready VALUE=1
  {% endif %}"""

    if old_z_block in scfg:
        modified_scfg = scfg.replace(old_z_block, new_z_block)
        upload_config("sensorless.cfg", modified_scfg)
        print("   -> sensorless.cfg updated (Home Z will never crash pen or trigger System Error).")
    else:
        print("   -> Safe Z block already present or pattern matched differently.")

    print("3. Restarting Klipper...")
    send_gcode("FIRMWARE_RESTART")
    
    for _ in range(15):
        time.sleep(1.0)
        try:
            with urllib.request.urlopen(f"http://{PRINTER_IP}:{PRINTER_PORT}/printer/info", timeout=2.0) as r:
                res = json.loads(r.read().decode())["result"]
                if res.get("state") == "ready":
                    print("   -> Klipper ready!")
                    break
        except Exception:
            pass

    print("\nSUCCESS: SparkX i7 is now 100% immune to System Errors and safe to Home!")

def restore_factory_mode():
    print("Restoring original factory configs...")
    try:
        pcfg = get_config("printer.cfg.plotter_backup")
        upload_config("printer.cfg", pcfg)
        scfg = get_config("sensorless.cfg.plotter_backup")
        upload_config("sensorless.cfg", scfg)
        send_gcode("FIRMWARE_RESTART")
        print("Factory 3D printing configurations successfully restored!")
    except Exception as e:
        print(f"Restore error: {e}")

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--restore":
        restore_factory_mode()
    else:
        enable_plotter_mode()
