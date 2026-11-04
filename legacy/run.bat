@echo off
where pythonw >nul 2>nul
if %errorlevel%==0 (
  start "" pythonw "%~dp0app.py"
) else (
  start "" "%LOCALAPPDATA%\Programs\Python\Python314\pythonw.exe" "%~dp0app.py"
)
