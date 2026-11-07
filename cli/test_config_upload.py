import urllib.request
import uuid

def test():
    boundary = "----WebKitFormBoundary" + uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"root\"\r\n\r\nconfig\r\n".encode("utf-8"),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"test_plotter.txt\"\r\nContent-Type: text/plain\r\n\r\nplotter test\r\n".encode("utf-8"),
        f"--{boundary}--\r\n".encode("utf-8")
    ]
    data = b"".join(parts)
    req = urllib.request.Request(
        "http://192.168.78.50:7125/server/files/upload",
        data=data,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST"
    )
    with urllib.request.urlopen(req, timeout=5.0) as r:
        print("Response:", r.getcode())

if __name__ == "__main__":
    test()

