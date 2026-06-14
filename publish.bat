@echo off
REM Build the single, self-contained Windows .exe (no .NET install needed to run it).
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
echo.
echo Output: bin\Release\net10.0\win-x64\publish\VideoCaptureCardViewer.exe
