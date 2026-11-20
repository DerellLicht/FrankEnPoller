::  build script for FrankEnPoller app

@if /I "%~1"=="build" goto :build
@if /I "%~1"=="execute" goto :execute

:usage
   @echo USAGE:
   @echo     build [build] [execute] 
   @echo.
   @echo ARGUMENTS
   @echo    build - run build command 
   @echo    execute - run the program
   @echo.
   @echo    Either build or execute are required
   @echo.
   @echo    Example: 
   @echo    build build 
   @goto :eof

:build
   dotnet publish -c Release -o publish
   @goto :eof

:execute
   @start "" publish\FrankEnPoller
   @goto :eof

