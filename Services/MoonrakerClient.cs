using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    #region Printer Hardware Mode Management (Plotter vs 3D Printing)

    public async Task<string?> GetConfigFileAsync(string ip, int port, string filename)
    {
        try
        {
            string url = $"http://{ip}:{port}/server/files/config/{filename}";
            var res = await _http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> UploadConfigFileAsync(string ip, int port, string filename, string content)
    {
        try
        {
            string url = $"http://{ip}:{port}/server/files/upload";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("config"), "root");

            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(fileContent, "file", filename);

            var res = await _http.PostAsync(url, form);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PrinterHardwareMode> CheckHardwareModeAsync(string ip, int port)
    {
        try
        {
            string? scfg = await GetConfigFileAsync(ip, port, "sensorless.cfg");
            if (string.IsNullOrEmpty(scfg)) return PrinterHardwareMode.Unknown;

            if (scfg.Contains("SAFE PLOTTER HOME Z") || scfg.Contains("SET_KINEMATIC_POSITION Z=25.0"))
            {
                return PrinterHardwareMode.PenPlotter;
            }
            if (scfg.Contains("PRES_CHECK") && scfg.Contains("_HOME_Z"))
            {
                return PrinterHardwareMode.ThreeDPrinting;
            }
            return PrinterHardwareMode.Unknown;
        }
        catch
        {
            return PrinterHardwareMode.Unknown;
        }
    }

    public async Task<(bool Success, string Message)> SetPlotterModeAsync(string ip, int port)
    {
        try
        {
            // 1. Fetch sensorless.cfg
            string? scfg = await GetConfigFileAsync(ip, port, "sensorless.cfg");
            if (string.IsNullOrEmpty(scfg))
            {
                return (false, "Impossibile scaricare sensorless.cfg dalla stampante.");
            }

            // 2. Ensure backup exists
            string? backup = await GetConfigFileAsync(ip, port, "sensorless.cfg.plotter_backup");
            if (string.IsNullOrEmpty(backup))
            {
                await UploadConfigFileAsync(ip, port, "sensorless.cfg.plotter_backup", scfg);
            }

            // 3. Patch Z homing block to be 100% safe for pen
            string newZBlock = 
@"  {% if home_all or 'Z' in params %}
    # SAFE PLOTTER HOME Z: Prevents pen crash into bed and System Error
    SET_KINEMATIC_POSITION Z=25.0
    G90
    G1 Z25.0 F1200
    SET_GCODE_VARIABLE MACRO=xyz_ready VARIABLE=z_ready VALUE=1
  {% endif %}";

            string modifiedScfg;
            var match = Regex.Match(
                scfg, 
                @"[ \t]*\{% if home_all or 'Z' in params %\}.*?\{% endif %\}", 
                RegexOptions.Singleline);

            if (match.Success)
            {
                modifiedScfg = scfg.Substring(0, match.Index) + newZBlock + scfg.Substring(match.Index + match.Length);
            }
            else
            {
                return (false, "Blocco 'home_all or Z in params' non trovato in sensorless.cfg.");
            }

            bool okUploadS = await UploadConfigFileAsync(ip, port, "sensorless.cfg", modifiedScfg);
            if (!okUploadS)
            {
                return (false, "Errore durante il caricamento di sensorless.cfg modificato.");
            }

            // 4. In printer.cfg, ensure pause_on_runout: false during plotter mode
            string? pcfg = await GetConfigFileAsync(ip, port, "printer.cfg");
            if (!string.IsNullOrEmpty(pcfg))
            {
                string? pbackup = await GetConfigFileAsync(ip, port, "printer.cfg.plotter_backup");
                if (string.IsNullOrEmpty(pbackup))
                {
                    await UploadConfigFileAsync(ip, port, "printer.cfg.plotter_backup", pcfg);
                }
                string modPcfg = Regex.Replace(
                    pcfg, 
                    @"(\[filament_switch_sensor filament_sensor\]\s*\n\s*pause_on_runout:\s*)true", 
                    @"$1false", 
                    RegexOptions.IgnoreCase);
                await UploadConfigFileAsync(ip, port, "printer.cfg", modPcfg);
            }

            // 5. Send FIRMWARE_RESTART
            await SendGCodeAsync(ip, port, "FIRMWARE_RESTART");

            // 6. Wait for Klipper ready
            bool isReady = await WaitForKlipperReadyAsync(ip, port, 25);
            if (isReady)
            {
                return (true, "Modalità Pen Plotter attivata con successo!\nZ protetto (Home Z senza tastatore su piatto) e sensori disattivati.");
            }
            else
            {
                return (true, "Configurazione inviata. Se lo schermo della stampante mostra 'restart', toccalo per ricollegare l'interfaccia.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Errore configurazione Plotter: {ex.Message}");
        }
    }

    public async Task<(bool Success, string Message)> Set3DPrintingModeAsync(string ip, int port)
    {
        try
        {
            // 1. Restore sensorless.cfg from backup if available, or factory block
            string? backup = await GetConfigFileAsync(ip, port, "sensorless.cfg.plotter_backup");
            bool okRestoreS = false;
            if (!string.IsNullOrEmpty(backup))
            {
                okRestoreS = await UploadConfigFileAsync(ip, port, "sensorless.cfg", backup);
            }
            else
            {
                string? scfg = await GetConfigFileAsync(ip, port, "sensorless.cfg");
                if (!string.IsNullOrEmpty(scfg))
                {
                    string factoryZBlock = 
@"  {% if home_all or 'Z' in params %}
    PRES_CHECK
    G4 P1000
    _HOME_Z
    BED_MESH_PROFILE LOAD=""default""
  {% endif %}";
                    var match = Regex.Match(
                        scfg, 
                        @"[ \t]*\{% if home_all or 'Z' in params %\}.*?\{% endif %\}", 
                        RegexOptions.Singleline);
                    if (match.Success)
                    {
                        string restoredScfg = scfg.Substring(0, match.Index) + factoryZBlock + scfg.Substring(match.Index + match.Length);
                        okRestoreS = await UploadConfigFileAsync(ip, port, "sensorless.cfg", restoredScfg);
                    }
                }
            }

            if (!okRestoreS)
            {
                return (false, "Impossibile ripristinare sensorless.cfg originale.");
            }

            // 2. Restore printer.cfg (switch_pin: !nozzle_mcu:PA1 and pause_on_runout: true)
            string? pcfg = await GetConfigFileAsync(ip, port, "printer.cfg");
            if (!string.IsNullOrEmpty(pcfg))
            {
                string restoredPcfg = pcfg.Replace("switch_pin: nozzle_mcu:PA1", "switch_pin: !nozzle_mcu:PA1");
                restoredPcfg = Regex.Replace(
                    restoredPcfg, 
                    @"(\[filament_switch_sensor filament_sensor\]\s*\n\s*pause_on_runout:\s*)false", 
                    @"$1true", 
                    RegexOptions.IgnoreCase);
                await UploadConfigFileAsync(ip, port, "printer.cfg", restoredPcfg);
            }

            // 3. Send FIRMWARE_RESTART
            await SendGCodeAsync(ip, port, "FIRMWARE_RESTART");

            // 4. Wait for Klipper ready
            bool isReady = await WaitForKlipperReadyAsync(ip, port, 25);
            if (isReady)
            {
                return (true, "Modalità Stampa 3D ripristinata con successo!\nTutti i sensori di fabbrica e il livellamento piatto originale sono ora attivi.");
            }
            else
            {
                return (true, "Configurazione 3D inviata. Se lo schermo della stampante mostra 'restart', toccalo per ricollegare l'interfaccia.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Errore ripristino 3D: {ex.Message}");
        }
    }

    private async Task<bool> WaitForKlipperReadyAsync(string ip, int port, int timeoutSeconds)
    {
        for (int i = 0; i < timeoutSeconds; i++)
        {
            await Task.Delay(1000);
            try
            {
                string url = $"http://{ip}:{port}/printer/info";
                var res = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(res);
                string state = doc.RootElement.GetProperty("result").GetProperty("state").GetString() ?? "";
                if (state.Equals("ready", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Wait for network/server reboot
            }
        }
        return false;
    }

    #endregion
}

