import urllib.request
import urllib.error
import uuid

def test():
    boundary = "----WebKitFormBoundary" + uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"print\"\r\n\r\ntrue\r\n".encode("utf-8"),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"test_plotter.gcode\"\r\nContent-Type: application/octet-stream\r\n\r\nG21\r\n".encode("utf-8"),
        f"--{boundary}--\r\n".encode("utf-8")
    ]
    data = b"".join(parts)
    req = urllib.request.Request(
        "http://192.168.78.50:7125/server/files/upload",
        data=data,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST"
    )
    try:
        with urllib.request.urlopen(req, timeout=5.0) as r:
            print("Response:", r.getcode(), r.read().decode())
    except urllib.error.HTTPError as e:
        print("HTTP Error:", e.code, e.read().decode())
    except Exception as e:
        print("Error:", e)

if __name__ == "__main__":
    test()

