```text
@echo off
setlocal
cd /d "%~dp0"

if exist publish rmdir /s /q publish

dotnet publish WarframeTracker.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o publish

if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

copy /Y index.html publish\index.html

if errorlevel 1 (
    echo HTML COPY FAILED
    pause
    exit /b 1
)

echo BUILD SUCCESSFUL
echo Output: publish
pause
``````text
@echo off
setlocal
cd /d "%~dp0"

if exist publish rmdir /s /q publish

dotnet publish WarframeTracker.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o publish

if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

copy /Y index.html publish\index.html

if errorlevel 1 (
    echo HTML COPY FAILED
    pause
    exit /b 1
)

echo BUILD SUCCESSFUL
echo Output: publish
pause
```