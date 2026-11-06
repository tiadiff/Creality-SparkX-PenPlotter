using System.Net.Http;
using System.Text;
using System.Text.Json;
using SparkXPenPlotter.Models;

namespace SparkXPenPlotter.Services;

public class MoonrakerClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<PrinterStateInfo> GetStatusAsync(string ip, int port = 7125)
    {
        var info = new PrinterStateInfo();
        try
        {
            string url = $"http://{ip}:{port}/printer/objects/query?print_stats&toolhead&filament_switch_sensor%20filament_sensor";
            var res = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(res);
            var status = doc.RootElement.GetProperty("result").GetProperty("status");

            info.IsConnected = true;

            if (status.TryGetProperty("print_stats", out var printStats))
            {
                info.State = printStats.GetProperty("state").GetString() ?? "Unknown";
                info.Filename = printStats.GetProperty("filename").GetString() ?? "";
                if (printStats.TryGetProperty("progress", out var prog))
                {
                    info.Progress = prog.GetDouble() * 100.0;
                }
            }

            if (status.TryGetProperty("toolhead", out var toolhead))
            {
                if (toolhead.TryGetProperty("position", out var pos) && pos.GetArrayLength() >= 3)
                {
                    info.X = Math.Round(pos[0].GetDouble(), 2);
                    info.Y = Math.Round(pos[1].GetDouble(), 2);
                    info.Z = Math.Round(pos[2].GetDouble(), 2);
                }
            }

            if (status.TryGetProperty("filament_switch_sensor filament_sensor", out var filSensor))
            {
                if (filSensor.TryGetProperty("filament_detected", out var detected))
                {
                    info.FilamentDetected = detected.GetBoolean();
                }
            }
        }
        catch
        {
            info.IsConnected = false;
            info.State = "Non raggiungibile";
        }
        return info;
    }

    public async Task<(bool Success, string Message)> UploadAndPrintAsync(
        string ip, 
        int port, 
        string filename, 
        string gcodeContent, 
        bool printNow = true)
    {
        try
        {
            // 1. Upload G-code file without starting print immediately to avoid blocking upload timeouts
            string uploadUrl = $"http://{ip}:{port}/server/files/upload";
            using var form = new MultipartFormDataContent();

            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(gcodeContent));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", filename);

            var uploadRes = await _http.PostAsync(uploadUrl, form);
            if (!uploadRes.IsSuccessStatusCode)
            {
                string errText = await uploadRes.Content.ReadAsStringAsync();
                return (false, $"Errore caricamento file: {uploadRes.StatusCode} - {errText}");
            }

            if (!printNow)
            {
                return (true, "File caricato sulla stampante con successo.");
            }

            // 2. Start print job via Moonraker print start API
            string startUrl = $"http://{ip}:{port}/printer/print/start?filename={Uri.EscapeDataString(filename)}";
            var startRes = await _http.PostAsync(startUrl, null);
            if (!startRes.IsSuccessStatusCode)
            {
                // Fallback to JSON payload if query parameter is not accepted by older Moonraker
                var jsonContent = new StringContent(
                    JsonSerializer.Serialize(new { filename }), 
                    Encoding.UTF8, 
                    "application/json");
                startRes = await _http.PostAsync($"http://{ip}:{port}/printer/print/start", jsonContent);
            }

            if (startRes.IsSuccessStatusCode)
            {
                return (true, "Disegno avviato con successo.");
            }
            else
            {
                string errText = await startRes.Content.ReadAsStringAsync();
                return (false, $"File caricato, ma avvio stampa fallito: {startRes.StatusCode} - {errText}");
            }
        }
        catch (TaskCanceledException)
        {
            return (false, "Timeout di connessione con la stampante (oltre 30s).");
        }
        catch (Exception ex)
        {
            return (false, $"Errore di rete: {ex.Message}");
        }
    }

    public async Task<bool> SendGCodeAsync(string ip, int port, string command)
    {
        try
        {
            string url = $"http://{ip}:{port}/printer/gcode/script";
            var payload = JsonSerializer.Serialize(new { script = command });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var res = await _http.PostAsync(url, content);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PauseAsync(string ip, int port)
    {
        try
        {
            var res = await _http.PostAsync($"http://{ip}:{port}/printer/print/pause", null);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> ResumeAsync(string ip, int port)
    {
        try
        {
            var res = await _http.PostAsync($"http://{ip}:{port}/printer/print/resume", null);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<bool> CancelAsync(string ip, int port)
    {
        try
        {
            var res = await _http.PostAsync($"http://{ip}:{port}/printer/print/cancel", null);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

