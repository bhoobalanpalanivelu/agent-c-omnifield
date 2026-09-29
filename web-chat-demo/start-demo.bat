@echo off
REM Serves this folder at http://localhost:8080 so the Maya widget gets a real origin (not file://)
cd /d "%~dp0"
start "" http://localhost:8080/maya-field-service.html
where python >nul 2>nul && (python -m http.server 8080 & goto :eof)
where py >nul 2>nul && (py -m http.server 8080 & goto :eof)
where npx >nul 2>nul && (npx --yes serve -l 8080 . & goto :eof)
echo Neither Python nor Node found. Install one, or use VS Code Live Server.
pause
