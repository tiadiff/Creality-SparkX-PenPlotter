@echo off
setlocal

echo ======================================================
echo    SPARKX i7 - PEN PLOTTER G-CODE GENERATOR
echo ======================================================
echo.

if "%~1"=="" (
    echo Nessun file SVG specificato. Genero il test di calibrazione...
    python pen_plotter.py --dry-run
    python pen_plotter.py
    echo.
    echo Creati:
    echo  - sample_calibration_dryrun.gcode (Test in aria Z=10mm)
    echo  - sample_calibration_draw.gcode   (Disegno reale Z=5mm)
    echo  - sample_calibration_preview.html (Anteprima visuale)
    start sample_calibration_preview.html
) else (
    echo Elaborazione file: %~nx1
    echo Genero versione Dry-Run (Test di sicurezza in aria)...
    python pen_plotter.py "%~1" --dry-run
    echo Genero versione Reale (Disegno su carta)...
    python pen_plotter.py "%~1"
    
    set "PREVIEW_FILE=%~dpn1_preview.html"
    if exist "%PREVIEW_FILE%" (
        echo Apro anteprima nel browser...
        start "" "%PREVIEW_FILE%"
    )
)

echo.
echo ======================================================
echo Operazione completata!
echo ======================================================
pause

