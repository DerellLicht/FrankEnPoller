# Makefile for FrankEnPoller (replaces build.cmd)
#
#   make                  stop any running copy, then publish a Release build into .\publish
#   make execute          launch the published program
#   make all execute      both, in that order
#   make usage            print this summary
#   make stop             just terminate a running copy
#   make clean            remove publish, bin and obj
#
# SHELL is deliberately left alone; Windows commands go through `cmd /c "..."`.
# tools/stop_app.py is the CubicAdventurer helper, extended with an optional graceful-exit command.

APP         := FrankEnPoller
PUBLISH_DIR := publish
EXE         := $(PUBLISH_DIR)\$(APP).exe
PYTHON      ?= python

# usage is listed first for readability, so name the default goal explicitly
.DEFAULT_GOAL := all

.PHONY: all usage execute stop clean

usage:
	@cmd /c "echo USAGE:"
	@cmd /c "echo     make [all] [execute]"
	@cmd /c echo.
	@cmd /c "echo TARGETS"
	@cmd /c "echo     all     - stop any running copy, then dotnet publish (Release); the default"
	@cmd /c "echo     execute - run the published program"
	@cmd /c "echo     stop    - terminate a running copy"
	@cmd /c "echo     clean   - remove publish, bin and obj folders"
	@cmd /c echo.
	@cmd /c "echo     Example: make all execute"

all: stop
	dotnet publish -c Release -o $(PUBLISH_DIR)

execute:
	@cmd /c start "" "$(EXE)"
	
wc:
	wc -l *.cs *.csproj	

# the exe cannot be replaced while it is running; this stops it only if it is running.
# "--exit" asks the program to quit through its own Exit path first, so its tray icon is
# removed; stop_app.py falls back to taskkill /F if it does not go away.
stop:
	@$(PYTHON) tools/stop_app.py $(APP) "$(EXE)" --exit

clean:
	@cmd /c "if exist $(PUBLISH_DIR) rmdir /s /q $(PUBLISH_DIR)"
	@cmd /c "if exist bin rmdir /s /q bin"
	@cmd /c "if exist obj rmdir /s /q obj"
