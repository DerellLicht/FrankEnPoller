# Makefile for FrankEnPoller (replaces build.cmd)
#
#   make build            stop any running copy, then publish a Release build into .\publish
#   make execute          launch the published program
#   make build execute    both, in that order
#   make stop             just terminate a running copy
#   make clean            remove publish, bin and obj
#
# SHELL is deliberately left alone; Windows commands go through `cmd /c "..."`.
# tools/stop_app.py is the same helper used by the CubicAdventurer Makefile.

APP         := FrankEnPoller
PUBLISH_DIR := publish
EXE         := $(PUBLISH_DIR)\$(APP).exe
PYTHON      ?= python

.PHONY: usage build execute stop clean

usage:
	@cmd /c "echo USAGE:"
	@cmd /c "echo     make build [execute]"
	@cmd /c echo.
	@cmd /c "echo TARGETS"
	@cmd /c "echo     build   - stop any running copy, then dotnet publish (Release)"
	@cmd /c "echo     execute - run the published program"
	@cmd /c "echo     stop    - terminate a running copy"
	@cmd /c "echo     clean   - remove publish, bin and obj folders"
	@cmd /c echo.
	@cmd /c "echo     Example: make build execute"

build: stop
	dotnet publish -c Release -o $(PUBLISH_DIR)

execute:
	@cmd /c start "" "$(EXE)"

# the exe cannot be replaced while it is running; this stops it only if it is running
stop:
	@$(PYTHON) tools/stop_app.py $(APP)

clean:
	@cmd /c "if exist $(PUBLISH_DIR) rmdir /s /q $(PUBLISH_DIR)"
	@cmd /c "if exist bin rmdir /s /q bin"
	@cmd /c "if exist obj rmdir /s /q obj"
