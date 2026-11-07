import os
import sys
import uuid
import urllib.request
import json

PRINTER_IP = "192.168.78.50"
PRINTER_PORT = 7125

def upload_gcode(file_path, print_now=False):
    filename = os.path.basename(file_path)
    if not os.path.exists(file_path):
        print(f"Error: {file_path} not found")
        return False

    with open(file_path, "rb") as f:
        file_bytes = f.read()

    boundary = f"----WebKitFormBoundary{uuid.uuid4().hex}"
    
    parts = []
    if print_now:
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"print\"\r\n\r\ntrue\r\n".encode("utf-8"))
        
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\nContent-Type: application/octet-stream\r\n\r\n".encode("utf-8"))
    parts.append(file_bytes)
    parts.append(f"\r\n--{boundary}--\r\n".encode("utf-8"))
    
    payload = b"".join(parts)
    url = f"http://{PRINTER_IP}:{PRINTER_PORT}/server/files/upload"
    
    req = urllib.request.Request(
        url,
        data=payload,
        headers={
            "Content-Type": f"multipart/form-data; boundary={boundary}",
            "Content-Length": str(len(payload))
        },
        method="POST"
    )
    
    try:
        with urllib.request.urlopen(req, timeout=15.0) as res:
            res_data = json.loads(res.read().decode("utf-8"))
            print(f"Successfully uploaded {filename} to {PRINTER_IP}")
            if print_now:
                print(f"Print job started on printer!")
            return True
    except Exception as e:
        print(f"Failed to upload: {e}")
        return False

if __name__ == "__main__":
    action = sys.argv[1] if len(sys.argv) > 1 else "upload_both"
    
    if action == "dryrun":
        print("Uploading and STARTING Dry-Run...")
        upload_gcode("3130_hornet_dryrun.gcode", print_now=True)
    elif action == "draw":
        print("Uploading and STARTING Real Drawing...")
        upload_gcode("3130_hornet_draw.gcode", print_now=True)
    elif action == "upload_only":
        print("Uploading both files without starting...")
        upload_gcode("3130_hornet_dryrun.gcode", print_now=False)
        upload_gcode("3130_hornet_draw.gcode", print_now=False)
    else:
        # Default: upload both, start the real drawing as requested by user
        upload_gcode("3130_hornet_dryrun.gcode", print_now=False)
        upload_gcode("3130_hornet_draw.gcode", print_now=True)

